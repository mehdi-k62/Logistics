using Replanning.Domain;

namespace Replanning.Application;

public interface IReplanningProcessRepository
{
    Task<ReplanningProcess?> GetAsync(Guid id, CancellationToken ct);
    Task<bool> TryClaimAsync(Guid id, string owner, DateTimeOffset leaseUntil, long expectedVersion, CancellationToken ct);
    Task SaveAsync(ReplanningProcess process, CancellationToken ct);
    Task<IReadOnlyList<ReplanningProcess>> GetDueAsync(DateTimeOffset now, int batchSize, CancellationToken ct);
}

public interface IBookingGateway
{
    Task<BookingSnapshot?> GetAsync(string bookingId, CancellationToken ct);
    Task ReassignAsync(string bookingId, string voyageId, DateTimeOffset now, CancellationToken ct);
}

public sealed record BookingSnapshot(string BookingId, int RequestedTeu, string? ActiveVoyageId, bool IsActive);

public interface IVoyageGateway
{
    Task<IReadOnlyList<VoyageCandidate>> FindAlternativesAsync(string bookingId,int requestedTeu, string? preferredVoyageId, CancellationToken ct);
    Task<ReservationResult> ReserveAsync(string voyageId, string bookingId, Guid processId, int units, CancellationToken ct);
    Task ReleaseAsync(Guid reservationId, CancellationToken ct);
    Task ReleaseForBookingVoyageAsync(string bookingId, string voyageId, CancellationToken ct);
}

public sealed record VoyageCandidate(string VoyageId, int AvailableTeu);
public sealed record ReservationResult(Guid ReservationId, bool Created, bool AlreadyExists,bool capacityUnavailable=false);

public interface IReplanningClock { DateTimeOffset UtcNow { get; } }
public interface IRetryPolicy
{
    DateTimeOffset NextAttempt(int retryCount, DateTimeOffset now);
}


public interface IWorkflowFailureInjector
{
    Task AfterCapacityReservationAsync(Guid processId, string voyageId, CancellationToken ct);
}

public sealed class NoopWorkflowFailureInjector : IWorkflowFailureInjector
{
    public Task AfterCapacityReservationAsync(Guid processId, string voyageId, CancellationToken ct) => Task.CompletedTask;
}

public sealed class SimulatedCrashException : Exception
{
    public SimulatedCrashException(Guid processId)
        : base($"Simulated worker crash after capacity reservation for process {processId}.") { }
}

public sealed class CapacityUnavailableException(string voyageId) : Exception($"Capacity unavailable for voyage {voyageId}.");
