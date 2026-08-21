using Logistics.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Logistics.IntegrationTests;

public sealed class OutboxFailureTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("LOGISTICS_TEST_CONNECTION_STRING")
        ?? "Server=192.168.33.37,1433;Database=Logistics;User Id=sa;Password=Mehd!K@1;TrustServerCertificate=True;Encrypt=False";

    [Fact]
    public async Task Broker_failure_leaves_message_pending_and_retryable()
    {
        var options = new DbContextOptionsBuilder<LogisticsDbContext>().UseSqlServer(ConnectionString).Options;
        var messageId = Guid.NewGuid();

        await using (var setup = new LogisticsDbContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.OutboxMessages.Add(OutboxMessage.Create("Test.Event", Guid.NewGuid(), "{}"));
            await setup.SaveChangesAsync();
        }

        var publisher = new RecordingPublisher { Fail = true };
        await using (var db = new LogisticsDbContext(options))
        {
            var dispatcher = new OutboxDispatcher(db, publisher, NullLogger<OutboxDispatcher>.Instance);
            var result = await dispatcher.DispatchOnceAsync(10, CancellationToken.None);
            Assert.Equal(0, result);
        }

        await using var verify = new LogisticsDbContext(options);
        var row = await verify.OutboxMessages.OrderByDescending(x => x.OccurredAt).FirstAsync();
        Assert.Equal(OutboxStatus.Pending, row.Status);
        Assert.Equal(1, row.Attempts);
        Assert.NotNull(row.LastError);
        Assert.Equal(1, publisher.Calls);
    }

    [Fact]
    public async Task Crash_after_broker_publish_before_marking_published_is_at_least_once()
    {
        var options = new DbContextOptionsBuilder<LogisticsDbContext>().UseSqlServer(ConnectionString).Options;
        await using (var setup = new LogisticsDbContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
            var message = OutboxMessage.Create("VoyageDisrupted", Guid.NewGuid(), "{\"event\":\"disrupted\"}");
            setup.OutboxMessages.Add(message);
            await setup.SaveChangesAsync();
        }

        // First broker call succeeds, but the publisher throws AFTER delivery.
        // This models process death/network ambiguity between broker publish and DB acknowledgement.
        var broker = new CrashAfterDeliveryPublisher();
        await using (var firstDb = new LogisticsDbContext(options))
        {
            var dispatcher = new OutboxDispatcher(firstDb, broker, NullLogger<OutboxDispatcher>.Instance,
                owner: "worker-1", lease: TimeSpan.FromSeconds(1));
            await dispatcher.DispatchOnceAsync(10, CancellationToken.None);
        }

        Assert.Equal(1, broker.Deliveries);

        // The failed attempt is immediately returned to Pending by the dispatcher.
        // A second worker can therefore publish it again. The consumer must deduplicate.
        broker.Throw = false;
        await using (var secondDb = new LogisticsDbContext(options))
        {
            var dispatcher = new OutboxDispatcher(secondDb, broker, NullLogger<OutboxDispatcher>.Instance,
                owner: "worker-2", lease: TimeSpan.FromSeconds(1));
            var published = await dispatcher.DispatchOnceAsync(10, CancellationToken.None);
            Assert.Equal(1, published);
        }

        Assert.Equal(2, broker.Deliveries);

        await using var verify = new LogisticsDbContext(options);
        var row = await verify.OutboxMessages.OrderByDescending(x => x.OccurredAt).FirstAsync();
        Assert.Equal(OutboxStatus.Published, row.Status);
        Assert.NotNull(row.PublishedAt);
    }

    private sealed class RecordingPublisher : IMessagePublisher
    {
        public bool Fail { get; set; }
        public int Calls { get; private set; }
        public Task PublishAsync(OutboxMessage message, CancellationToken ct)
        {
            Calls++;
            if (Fail) throw new InvalidOperationException("Simulated broker outage.");
            return Task.CompletedTask;
        }
    }

    private sealed class CrashAfterDeliveryPublisher : IMessagePublisher
    {
        public bool Throw { get; set; } = true;
        public int Deliveries { get; private set; }
        public Task PublishAsync(OutboxMessage message, CancellationToken ct)
        {
            Deliveries++;
            if (Throw) throw new SimulatedCrashAfterDeliveryException();
            return Task.CompletedTask;
        }
    }

    private sealed class SimulatedCrashAfterDeliveryException : Exception;
}
