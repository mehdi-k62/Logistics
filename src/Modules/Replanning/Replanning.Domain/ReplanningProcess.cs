using Logistics.SharedKernel;

namespace Replanning.Domain;

public enum ReplanningState
{
    Started = 1,
    EligibilityChecked = 2,
    AlternativeSelected = 3,
    CapacityReserved = 4,
    BookingReassigned = 5,
    OldCapacityReleasePending = 6,
    Completed = 7,
    RetryPending = 20,
    ManualReview = 30
}

public sealed class ReplanningProcess : AggregateRoot<Guid>
{
  

    public ReplanningProcess(Guid id, string bookingId, Guid disruptionId, string? currentVoyageId = null, string? requestedAlternativeVoyageId = null) : base(id)
    {
        BookingId = bookingId;
        DisruptionId = disruptionId;
        CurrentVoyageId = currentVoyageId;
        RequestedAlternativeVoyageId = requestedAlternativeVoyageId;
        State = ReplanningState.Started;
        CreatedAt = DateTimeOffset.UtcNow;
        UpdatedAt = CreatedAt;
    }

    public string BookingId { get; private set; }
    public Guid DisruptionId { get; private set; }
    public ReplanningState State { get; private set; }
    public string? CurrentVoyageId { get; private set; }
    public string? PreviousVoyageId { get; private set; }
    public string? TargetVoyageId { get; private set; }
    public string? RequestedAlternativeVoyageId { get; private set; }
    public int RetryCount { get; private set; }
    public DateTimeOffset? NextAttemptAt { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public long Version { get; private set; }
    public string? LeaseOwner { get; private set; }
    public DateTimeOffset? LeaseUntil { get; private set; }

    public void ConfirmEligibility()
    {
        EnsureState(ReplanningState.Started);
        Transition(ReplanningState.EligibilityChecked);
    }

    public void SelectAlternative(string voyageId)
    {
        EnsureState(ReplanningState.EligibilityChecked);
        TargetVoyageId = voyageId;
        Transition(ReplanningState.AlternativeSelected);
    }

    public void MarkCapacityReserved()
    {
        EnsureState(ReplanningState.AlternativeSelected);
        Transition(ReplanningState.CapacityReserved);
    }

    public void SetPreviousVoyage(string? voyageId) => PreviousVoyageId = voyageId;

    public void MarkBookingReassigned(string voyageId)
    {
        if (State is not ReplanningState.CapacityReserved and not ReplanningState.RetryPending)
            throw new InvalidOperationException($"Cannot reassign from {State}.");

        CurrentVoyageId = voyageId;
        Transition(ReplanningState.BookingReassigned);
    }

    public void MarkOldCapacityReleasePending()
    {
        if (State != ReplanningState.BookingReassigned)
            throw new InvalidOperationException($"Cannot release old capacity from {State}.");

        Transition(ReplanningState.OldCapacityReleasePending);
    }

    public void Complete()
    {
        if (State is ReplanningState.Completed)
            return;

        if (State is not ReplanningState.BookingReassigned and not ReplanningState.OldCapacityReleasePending)
            throw new InvalidOperationException($"Cannot complete from {State}.");

        Transition(ReplanningState.Completed);
        CompletedAt = DateTimeOffset.UtcNow;
        Raise(new ReplanningCompleted(Id, BookingId, TargetVoyageId, CompletedAt.Value));
    }

    public void ScheduleRetry(string error, DateTimeOffset nextAttempt)
    {
        if (State == ReplanningState.Completed)
            throw new InvalidOperationException("Completed process cannot be retried.");

        RetryCount++;
        LastError = error;
        NextAttemptAt = nextAttempt;
        State = ReplanningState.RetryPending;
        Touch();
    }

    public void MoveToManualReview(string error)
    {
        if (State == ReplanningState.Completed)
            throw new InvalidOperationException("Completed process cannot move to manual review.");

        LastError = error;
        State = ReplanningState.ManualReview;
        Touch();
    }

    public void ResumeFromRetry()
    {
        if (State != ReplanningState.RetryPending)
            throw new InvalidOperationException("Process is not waiting for retry.");

        // The worker should reload the persisted process and execute the next safe step.
        State = TargetVoyageId is null
            ? ReplanningState.EligibilityChecked
            : CurrentVoyageId is null
                ? ReplanningState.AlternativeSelected
                : ReplanningState.BookingReassigned;

        NextAttemptAt = null;
        Touch();
    }

    private void EnsureState(ReplanningState expected)
    {
        if (State != expected)
            throw new InvalidOperationException($"Expected {expected}, actual {State}.");
    }

    private void Transition(ReplanningState state)
    {
        State = state;
        Version++;
        Touch();
    }

    public void SetLease(string owner, DateTimeOffset until) { LeaseOwner = owner; LeaseUntil = until; Touch(); }
    public void ClearLease() { LeaseOwner = null; LeaseUntil = null; Touch(); }

    private void Touch() => UpdatedAt = DateTimeOffset.UtcNow;
}

public sealed record ReplanningCompleted(
    Guid ProcessId,
    string BookingId,
    string? TargetVoyageId,
    DateTimeOffset OccurredAt) : IDomainEvent;
