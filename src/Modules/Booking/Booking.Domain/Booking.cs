using Logistics.SharedKernel;

namespace Booking.Domain;

public enum BookingState { Active = 1, Cancelled = 2 }

public enum AssignmentState { Active = 1, Released = 2 }

public sealed record VoyageAssignment(
    string VoyageId,
    AssignmentState State,
    long Sequence,
    DateTimeOffset EffectiveAt);

public sealed class Booking : AggregateRoot<string>
{
    private Booking() : base("") { }

    public Booking(string id, int requestedTeu) : base(id)
    {
        if (requestedTeu <= 0) throw new ArgumentOutOfRangeException(nameof(requestedTeu));
        RequestedTeu = requestedTeu;
        State = BookingState.Active;
    }

    public int RequestedTeu { get; private set; }
    public BookingState State { get; private set; }
    public long Version { get; private set; }

    private readonly List<VoyageAssignment> _assignments = [];
    public IReadOnlyCollection<VoyageAssignment> Assignments => _assignments.AsReadOnly();

    public VoyageAssignment? ActiveAssignment =>
        _assignments.LastOrDefault(x => x.State == AssignmentState.Active);

    public void Reassign(string voyageId, DateTimeOffset now)
    {
        if (State != BookingState.Active)
            throw new InvalidOperationException("Cancelled booking cannot be reassigned.");

        var active = ActiveAssignment;
        if (active is not null && active.VoyageId == voyageId)
            return; // idempotent

        if (active is not null)
        {
            var index = _assignments.IndexOf(active);
            _assignments[index] = active with { State = AssignmentState.Released };
        }

        var nextSequence = _assignments.Count == 0 ? 1 : _assignments.Max(x => x.Sequence) + 1;
        _assignments.Add(new VoyageAssignment(voyageId, AssignmentState.Active, nextSequence, now));
        Version++;
        Raise(new BookingReassigned(Id, voyageId, nextSequence, now));
    }
}

public sealed record BookingReassigned(
    string BookingId,
    string VoyageId,
    long Sequence,
    DateTimeOffset OccurredAt) : IDomainEvent;
