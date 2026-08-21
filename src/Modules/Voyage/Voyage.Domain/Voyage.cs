using Logistics.SharedKernel;

namespace Voyage.Domain;

public enum VoyageState { Available = 1, Disrupted = 2, Cancelled = 3 }

public enum ReservationState { Active = 1, Released = 2 }

public sealed class Voyage : AggregateRoot<string>
{
    private Voyage() : base("") { }

    public Voyage(string id, int totalCapacity) : base(id)
    {
        if (totalCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(totalCapacity));
        TotalCapacity = totalCapacity;
        State = VoyageState.Available;
    }

    public VoyageState State { get; private set; }
    public int TotalCapacity { get; private set; }
    public int ReservedCapacity { get; private set; }
    public long Version { get; private set; }

    public void Disrupt()
    {
        if (State == VoyageState.Cancelled) return;
        State = VoyageState.Disrupted;
        Version++;
        Raise(new VoyageDisrupted(Id, DateTimeOffset.UtcNow));
    }
}

public sealed record VoyageDisrupted(string VoyageId, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed class CapacityReservation : Entity<Guid>
{

    public CapacityReservation(Guid id, string bookingId, string voyageId, Guid processId, int units)
        : base(id)
    {
        BookingId = bookingId;
        VoyageId = voyageId;
        ProcessId = processId;
        Units = units;
        State = ReservationState.Active;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public string BookingId { get; private set; }
    public string VoyageId { get; private set; }
    public Guid ProcessId { get; private set; }
    public int Units { get; private set; }
    public ReservationState State { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ReleasedAt { get; private set; }

    public void Release(DateTimeOffset now)
    {
        if (State == ReservationState.Released) return;
        State = ReservationState.Released;
        ReleasedAt = now;
    }
}
