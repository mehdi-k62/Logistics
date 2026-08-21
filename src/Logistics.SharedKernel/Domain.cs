namespace Logistics.SharedKernel;

public abstract class Entity<TId>(TId id)
{
    public TId Id { get; protected set; } = id;
}

public abstract class AggregateRoot<TId>(TId id) : Entity<TId>(id)
{
    private readonly List<IDomainEvent> _events = [];
    public IReadOnlyCollection<IDomainEvent> DomainEvents => _events.AsReadOnly();

    protected void Raise(IDomainEvent @event) => _events.Add(@event);
    public void ClearDomainEvents() => _events.Clear();
}

public interface IDomainEvent
{
    DateTimeOffset OccurredAt { get; }
}
