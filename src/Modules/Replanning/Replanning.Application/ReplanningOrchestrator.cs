using Microsoft.Extensions.Logging;
using Replanning.Domain;

namespace Replanning.Application;

public sealed class ReplanningOrchestrator(
    IReplanningProcessRepository processes,
    IBookingGateway bookings,
    IVoyageGateway voyages,
    IReplanningClock clock,
    IRetryPolicy retryPolicy,
    IWorkflowFailureInjector failureInjector,
    ILogger<ReplanningOrchestrator> logger)
{
    public async Task ExecuteAsync(Guid processId, CancellationToken ct)
    {
        var process = await processes.GetAsync(processId, ct)
            ?? throw new InvalidOperationException($"Process {processId} was not found.");

        if (process.State == ReplanningState.Completed)
            return;

        try
        {
            switch (process.State)
            {
                case ReplanningState.Started:
                    await CheckEligibility(process, ct);
                    break;
                case ReplanningState.EligibilityChecked:
                    await SelectAlternative(process, ct);
                    break;
                case ReplanningState.AlternativeSelected:
                    await Reserve(process, ct);
                    break;
                case ReplanningState.CapacityUnavailable:
                    await RetryUnavailableCapacity(process, ct);
                    break;
                case ReplanningState.CapacityReserved:
                    await Reassign(process, ct);
                    break;
                case ReplanningState.BookingReassigned:
                    await ReleaseOldCapacity(process, ct);
                    break;
                case ReplanningState.OldCapacityReleasePending:
                    await ReleaseOldCapacity(process, ct);
                    break;
                case ReplanningState.RetryPending:
                    process.ResumeFromRetry();
                    await processes.SaveAsync(process, ct);
                    await ExecuteAsync(process.Id, ct);
                    break;
                case ReplanningState.ManualReview:
                    return;
            }
        }
        catch (SimulatedCrashException)
        {
            // A real process crash would terminate the worker before it can persist the next state.
            // Let the exception escape so tests can verify restart/recovery semantics.
            throw;
        }
        catch (TransientReplanningException ex)
        {
            var next = retryPolicy.NextAttempt(process.RetryCount + 1, clock.UtcNow);
            process.ScheduleRetry(ex.Message, next);
            await processes.SaveAsync(process, ct);
            logger.LogWarning(ex, "Replanning {ProcessId} scheduled for retry at {NextAttempt}.", process.Id, next);
        }
        catch (Exception ex)
        {
            process.MoveToManualReview(ex.Message);
            await processes.SaveAsync(process, ct);
            logger.LogError(ex, "Replanning {ProcessId} moved to manual review.", process.Id);
        }
    }

    private async Task CheckEligibility(ReplanningProcess process, CancellationToken ct)
    {
        var booking = await bookings.GetAsync(process.BookingId, ct)
            ?? throw new InvalidOperationException("Booking not found.");
        if (!booking.IsActive)
            throw new InvalidOperationException("Booking is not eligible for replanning.");
        process.ConfirmEligibility();
        await processes.SaveAsync(process, ct);
    }

    private async Task RetryUnavailableCapacity(ReplanningProcess process, CancellationToken ct)
    {
        var booking = await bookings.GetAsync(process.BookingId, ct)
            ?? throw new InvalidOperationException("Booking not found.");
        if (!booking.IsActive)
            throw new InvalidOperationException("Booking is not eligible for replanning.");
        process.RetryUnavailabeCapacity();
        await processes.SaveAsync(process, ct);
    }

    private async Task SelectAlternative(ReplanningProcess process, CancellationToken ct)
    {
        var booking = await bookings.GetAsync(process.BookingId, ct)
            ?? throw new InvalidOperationException("Booking not found.");
        var candidates = await voyages.FindAlternativesAsync(process.BookingId,booking.RequestedTeu, process.CurrentVoyageId, ct);
        var selected = !string.IsNullOrWhiteSpace(process.RequestedAlternativeVoyageId)
            ? candidates.FirstOrDefault(x => x.VoyageId == process.RequestedAlternativeVoyageId)
            : candidates.OrderByDescending(x => x.AvailableTeu).FirstOrDefault();
        if (selected is null || selected.AvailableTeu < booking.RequestedTeu)
            throw new InvalidOperationException("No eligible alternative voyage with sufficient capacity.");
        process.SelectAlternative(selected.VoyageId);
        await processes.SaveAsync(process, ct);
    }

    private async Task Reserve(ReplanningProcess process, CancellationToken ct)
    {
        var booking = await bookings.GetAsync(process.BookingId, ct)
            ?? throw new InvalidOperationException("Booking not found.");
        if (process.TargetVoyageId is null)
            throw new InvalidOperationException("Target voyage is missing.");

        var result = await voyages.ReserveAsync(process.TargetVoyageId!, booking.BookingId, process.Id, booking.RequestedTeu, ct);
        //if capacity unavailabe due to race condition
        if (result.capacityUnavailable)
        {
            process.MarkCapacityUnavailabe();
            await processes.SaveAsync(process,ct);
            return;
        }

        if (!result.Created && !result.AlreadyExists)
            throw new TransientReplanningException("Capacity reservation could not be confirmed.");


        // Failure injection deliberately occurs BEFORE the process state is advanced.
        // If the process dies here, the durable state remains AlternativeSelected while
        // the reservation is already committed. The recovery attempt must detect the
        // existing reservation and continue without reserving capacity twice.
        await failureInjector.AfterCapacityReservationAsync(process.Id, process.TargetVoyageId!, ct);

        process.MarkCapacityReserved();
        await processes.SaveAsync(process, ct);
    }

    private async Task Reassign(ReplanningProcess process, CancellationToken ct)
    {
        if (process.TargetVoyageId is null)
            throw new InvalidOperationException("Target voyage is missing.");
        var booking = await bookings.GetAsync(process.BookingId, ct)
            ?? throw new InvalidOperationException("Booking not found.");
        var oldVoyage = process.CurrentVoyageId;
        if (oldVoyage is null)
            throw new InvalidOperationException("Replanning process has no current voyage.");
        if (string.Equals(oldVoyage, process.TargetVoyageId, StringComparison.Ordinal))
            throw new InvalidOperationException("Target voyage must differ from disrupted/current voyage.");

        // Recovery-safe reassignment: if the booking was already moved before the process
        // state was persisted, do not perform a second assignment. Advance the process
        // from the durable business state instead.
        if (string.Equals(booking.ActiveVoyageId, process.TargetVoyageId, StringComparison.Ordinal))
        {
            process.SetPreviousVoyage(oldVoyage);
            process.MarkBookingReassigned(process.TargetVoyageId!);
            await processes.SaveAsync(process, ct);
            return;
        }

        if (!string.Equals(booking.ActiveVoyageId, oldVoyage, StringComparison.Ordinal))
            throw new TransientReplanningException($"Booking current voyage changed from {oldVoyage} to {booking.ActiveVoyageId} before reassignment.");

        await bookings.ReassignAsync(process.BookingId, process.TargetVoyageId!, clock.UtcNow, ct);
        process.SetPreviousVoyage(oldVoyage);
        process.MarkBookingReassigned(process.TargetVoyageId!);
        await processes.SaveAsync(process, ct);
    }

    private async Task ReleaseOldCapacity(ReplanningProcess process, CancellationToken ct)
    {
        if (process.PreviousVoyageId != null)
        {
            process.MarkOldCapacityReleasePending();
            await processes.SaveAsync(process, ct);
            await voyages.ReleaseForBookingVoyageAsync(process.BookingId, process.PreviousVoyageId, ct);
        }
        process.Complete();
        await processes.SaveAsync(process, ct);
    }
}

public sealed class TransientReplanningException(string message) : Exception(message);
