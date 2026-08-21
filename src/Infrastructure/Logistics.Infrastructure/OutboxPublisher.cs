using Logistics.Infrastructure.Consumers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Logistics.Infrastructure;

public interface IMessagePublisher
{
    Task PublishAsync(OutboxMessage message, CancellationToken ct);
}

public sealed class InProcessMessagePublisher(
    VoyageDisruptedConsumer voyageDisruptedConsumer,
    ILogger<InProcessMessagePublisher> logger) : IMessagePublisher
{
    public async Task PublishAsync(OutboxMessage message, CancellationToken ct)
    {
        logger.LogInformation("Publishing outbox message {MessageId} type {Type} aggregate {AggregateId}",
            message.Id, message.MessageType, message.AggregateId);

        switch (message.MessageType)
        {
            case "VoyageDisrupted":
            {
                var payload = JsonSerializer.Deserialize<VoyageDisruptedMessage>(message.Payload)
                    ?? throw new InvalidOperationException($"Invalid payload for {message.MessageType}.");
                await voyageDisruptedConsumer.HandleAsync(payload, ct);
                return;
            }
            default:
                throw new InvalidOperationException($"No consumer registered for message type '{message.MessageType}'.");
        }
    }
}

/// <summary>
/// At-least-once dispatcher. Correctness does NOT depend on exactly-once publication.
/// A crash after broker publish but before marking the row Published may cause a duplicate.
/// Consumers must therefore be idempotent.
/// </summary>
public sealed class OutboxDispatcher
{
    private readonly LogisticsDbContext _db;
    private readonly IMessagePublisher _publisher;
    private readonly string _owner;
    private readonly TimeSpan _lease;
    private readonly ILogger<OutboxDispatcher> _logger;

    public OutboxDispatcher(LogisticsDbContext db, IMessagePublisher publisher, ILogger<OutboxDispatcher> logger,
        string? owner = null, TimeSpan? lease = null)
    {
        _db = db;
        _publisher = publisher;
        _logger = logger;
        _owner = owner ?? Guid.NewGuid().ToString("N");
        _lease = lease ?? TimeSpan.FromSeconds(30);
    }

    public async Task<int> DispatchOnceAsync(int batchSize, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        // Recover abandoned claims. This is deliberately separate from publishing:
        // a reclaimed row may already have reached the broker, so republishing is expected.
        await _db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE dbo.OutboxMessages
            SET Status = 'Pending', LeaseOwner = NULL, LeaseUntil = NULL
            WHERE Status = 'Processing'
              AND LeaseUntil < {now};
            """, ct);

        var ids = await _db.OutboxMessages
            .Where(x => x.Status == OutboxStatus.Pending && x.NextAttemptAt <= now)
            .OrderBy(x => x.OccurredAt)
            .Take(batchSize)
            .Select(x => x.Id)
            .ToListAsync(ct);

        var published = 0;
        foreach (var id in ids)
        {
            if (!await TryClaimAsync(id, now, ct))
                continue;

            var message = await _db.OutboxMessages.SingleAsync(x => x.Id == id, ct);
            try
            {
                await _publisher.PublishAsync(message, ct);

                // This DB update is intentionally AFTER PublishAsync. If the process dies
                // between those two operations, the message is delivered again after lease expiry.
                var updated = await _db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE dbo.OutboxMessages
                    SET Status = 'Published', PublishedAt = SYSUTCDATETIME(),
                        LeaseOwner = NULL, LeaseUntil = NULL
                    WHERE Id = {id} AND Status = 'Processing' AND LeaseOwner = {_owner};
                    """, ct);

                if (updated == 1)
                    published++;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogError(ex, "Outbox publish failed for {MessageId}.", id);
                await ScheduleRetryAsync(id, ex.Message, ct);
            }
        }
        return published;
    }

    private async Task<bool> TryClaimAsync(Guid id, DateTimeOffset now, CancellationToken ct)
    {
        var leaseUntil = now.Add(_lease);
        var rows = await _db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE dbo.OutboxMessages
            SET Status = 'Processing', LeaseOwner = {_owner}, LeaseUntil = {leaseUntil}
            WHERE Id = {id}
              AND Status = 'Pending'
              AND NextAttemptAt <= {now};
            """, ct);
        return rows == 1;
    }

    private Task<int> ScheduleRetryAsync(Guid id, string error, CancellationToken ct) =>
        _db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE dbo.OutboxMessages
            SET Attempts = Attempts + 1,
                Status = 'Pending',
                NextAttemptAt = DATEADD(second,
                    CASE WHEN Attempts < 8 THEN POWER(2, Attempts + 1) ELSE 300 END,
                    SYSUTCDATETIME()),
                LastError = {error},
                LeaseOwner = NULL,
                LeaseUntil = NULL
            WHERE Id = {id};
            """, ct);
}

public sealed class OutboxPublisherWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<OutboxPublisherWorker> logger,ILogger<OutboxDispatcher> dispatcherLogger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<LogisticsDbContext>();
                var publisher = scope.ServiceProvider.GetRequiredService<IMessagePublisher>();
                var dispatcher = new OutboxDispatcher(db, publisher, dispatcherLogger);
                await dispatcher.DispatchOnceAsync(50, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Outbox iteration failed.");
            }
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }
}
