using Logistics.Infrastructure;
using Logistics.Infrastructure.Consumers;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Replanning.Application;
using Replanning.Domain;
using Xunit;

namespace Logistics.IntegrationTests;

public sealed class DisruptionConsumerTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("LOGISTICS_TEST_CONNECTION_STRING")
        ?? "Server=192.168.33.37,1433;Database=Logistics;User Id=sa;Password=Mehd!K@1;TrustServerCertificate=True;Encrypt=False";

    [Fact]
    public async Task ChalengeTest()
    {
        await using (var cnn = new SqlConnection(ConnectionString))
        {
            await cnn.OpenAsync();
            await ExecuteAsync(cnn, $"""
                IF DB_ID('Logistics') IS NULL THROW 51000, 'Logistics database does not exist. Run database/schema.sql first.', 1;

                DELETE FROM dbo.CapacityReservations;
                DELETE FROM dbo.VoyageAssignments;
                DELETE FROM dbo.ReplanningProcesses;
                DELETE FROM dbo.Bookings;
                DELETE FROM dbo.Voyages;
                DELETE FROM dbo.ProcessedMessages;
                DELETE FROM dbo.IdempotencyRecords;
                DELETE FROM dbo.OutboxMessages;
                """);
        }
        var options = new DbContextOptionsBuilder<LogisticsDbContext>()
            .UseSqlServer(ConnectionString).Options;

        var disruptionId = Guid.NewGuid();

        await using (var db = new LogisticsDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            var v1 = new Voyage.Domain.Voyage("V100", 10);
            var v2= new Voyage.Domain.Voyage("V200", 4);
            var v3= new Voyage.Domain.Voyage("V300", 3);
            var v4= new Voyage.Domain.Voyage("V400", 6);
            var b1= new Booking.Domain.Booking("B001", 2);
            var b2 = new Booking.Domain.Booking("B002", 4);
            var b3= new Booking.Domain.Booking("B003", 1);

            b1.Reassign(v1.Id, DateTimeOffset.UtcNow);
            b2.Reassign(v1.Id, DateTimeOffset.UtcNow);
            b3.Reassign(v1.Id, DateTimeOffset.UtcNow);
            db.Voyages.AddRange(v1, v2, v3, v4);
            db.Bookings.AddRange(b1,b2,b3);
            await db.SaveChangesAsync();
        }

        var message = new VoyageDisruptedMessage(disruptionId, "V100", DateTimeOffset.UtcNow, 1);

        int first;
        int second;
        await using (var db = new LogisticsDbContext(options))
        {
            var consumer = new VoyageDisruptedConsumer(db);
            first = await consumer.HandleAsync(message, CancellationToken.None);
        }
        await using (var db = new LogisticsDbContext(options))
        {
            var consumer = new VoyageDisruptedConsumer(db);
            second = await consumer.HandleAsync(message, CancellationToken.None);
        }

        await using (var verify = new LogisticsDbContext(options))
        {
            Assert.Equal(3, first);
            Assert.Equal(0, second);
            Assert.Equal(1, await verify.ProcessedMessages.CountAsync(x => x.MessageId == disruptionId));
            Assert.Equal(1, await verify.ReplanningProcesses.CountAsync(x => x.DisruptionId == disruptionId && x.BookingId == "B001"));
            var p = await verify.ReplanningProcesses.Where(x => x.DisruptionId == disruptionId && x.BookingId == "B001").FirstOrDefaultAsync();
            
        }
    }

    [Fact]
    public async Task Out_of_order_old_voyage_disruption_does_not_create_replanning()
    {
        var options = new DbContextOptionsBuilder<LogisticsDbContext>()
            .UseSqlServer(ConnectionString).Options;

        var bookingId = "B001";
        var oldVoyageId = "V100";
        var targetVoyageId = "V200";
        var oldDisruptionId = Guid.NewGuid();

        await using (var db = new LogisticsDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            var booking = new Booking.Domain.Booking(bookingId, 1);
            booking.Reassign(oldVoyageId, DateTimeOffset.UtcNow);
            booking.Reassign(targetVoyageId, DateTimeOffset.UtcNow.AddSeconds(1));
            db.Voyages.Add(new Voyage.Domain.Voyage(oldVoyageId, 10));
            db.Voyages.Add(new Voyage.Domain.Voyage(targetVoyageId, 10));
            db.Bookings.Add(booking);
            await db.SaveChangesAsync();
        }

        await using (var db = new LogisticsDbContext(options))
        {
            var consumer = new VoyageDisruptedConsumer(db);
            var created = await consumer.HandleAsync(
                new VoyageDisruptedMessage(oldDisruptionId, oldVoyageId, DateTimeOffset.UtcNow, 1),
                CancellationToken.None);
            Assert.Equal(0, created);
        }

        await using (var verify = new LogisticsDbContext(options))
        {
            Assert.Equal(0, await verify.ReplanningProcesses.CountAsync(x => x.DisruptionId == oldDisruptionId));
        }
    }

    [Fact]
    public async Task Replacement_voyage_disruption_creates_new_replanning_process()
    {
        var options = new DbContextOptionsBuilder<LogisticsDbContext>()
            .UseSqlServer(ConnectionString).Options;

        var bookingId = "B001";
        var replacementVoyageId = "V300";
        var replacementDisruptionId = Guid.NewGuid();

        await using (var db = new LogisticsDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            var booking = new Booking.Domain.Booking(bookingId, 1);
            booking.Reassign(replacementVoyageId, DateTimeOffset.UtcNow);
            db.Voyages.Add(new Voyage.Domain.Voyage(replacementVoyageId, 10));
            db.Bookings.Add(booking);
            await db.SaveChangesAsync();
        }

        await using (var db = new LogisticsDbContext(options))
        {
            var consumer = new VoyageDisruptedConsumer(db);
            var created = await consumer.HandleAsync(
                new VoyageDisruptedMessage(replacementDisruptionId, replacementVoyageId, DateTimeOffset.UtcNow, 2),
                CancellationToken.None);
            Assert.Equal(1, created);
        }

        await using (var verify = new LogisticsDbContext(options))
        {
            var process = await verify.ReplanningProcesses.SingleAsync(x => x.DisruptionId == replacementDisruptionId);
            Assert.Equal(bookingId, process.BookingId);
            Assert.Equal(ReplanningState.Started, process.State);
        }
    }
    [Fact]
    public async Task Disruption_creates_process_and_replanning_moves_booking_to_a_different_voyage()
    {
        var options = new DbContextOptionsBuilder<LogisticsDbContext>()
            .UseSqlServer(ConnectionString).Options;

        var bookingId = $"B{Guid.NewGuid():N}"[..10];
        var oldVoyageId = $"V{Guid.NewGuid():N}"[..10];
        var newVoyageId = $"V{Guid.NewGuid():N}"[..10];
        var disruptionId = Guid.NewGuid();

        await using (var db = new LogisticsDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            var oldVoyage = new Voyage.Domain.Voyage(oldVoyageId, 10);
            oldVoyage.Disrupt();
            var newVoyage = new Voyage.Domain.Voyage(newVoyageId, 20);
            var booking = new Booking.Domain.Booking(bookingId, 1);
            booking.Reassign(oldVoyageId, DateTimeOffset.UtcNow);
            db.Voyages.AddRange(oldVoyage, newVoyage);
            db.Bookings.Add(booking);
            await db.SaveChangesAsync();
        }

        Guid processId;
        await using (var db = new LogisticsDbContext(options))
        {
            var consumer = new VoyageDisruptedConsumer(db);
            var created = await consumer.HandleAsync(
                new VoyageDisruptedMessage(disruptionId, oldVoyageId, DateTimeOffset.UtcNow, 1),
                CancellationToken.None);
            Assert.Equal(1, created);
            processId = await db.ReplanningProcesses
                .Where(x => x.DisruptionId == disruptionId && x.BookingId == bookingId)
                .Select(x => x.Id)
                .SingleAsync();
        }

        await using (var db = new LogisticsDbContext(options))
        {
            var orchestrator = new Replanning.Application.ReplanningOrchestrator(
                new ReplanningProcessRepository(db),
                new BookingGateway(db),
                new VoyageGateway(db),
                new SystemClock(),
                new ExponentialRetryPolicy(),
                new NoopWorkflowFailureInjector(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<Replanning.Application.ReplanningOrchestrator>.Instance);

            for (var i = 0; i < 8; i++)
            {
                await orchestrator.ExecuteAsync(processId, CancellationToken.None);
                var state = await db.ReplanningProcesses.Where(x => x.Id == processId).Select(x => x.State).SingleAsync();
                if (state == ReplanningState.Completed) break;
            }
        }

        await using (var verify = new LogisticsDbContext(options))
        {
            var booking = await verify.Bookings.Include(x => x.Assignments).SingleAsync(x => x.Id == bookingId);
            var process = await verify.ReplanningProcesses.SingleAsync(x => x.Id == processId);
            Assert.Equal(ReplanningState.Completed, process.State);
            Assert.NotEqual(oldVoyageId, booking.ActiveAssignment!.VoyageId);
            Assert.Equal(newVoyageId, booking.ActiveAssignment.VoyageId);
        }
    }


    private static async Task ExecuteAsync(SqlConnection connection, string sql)
    {
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 30 };
        await command.ExecuteNonQueryAsync();
    }
}
