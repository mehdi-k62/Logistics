using Logistics.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Replanning.Application;
using Replanning.Domain;
using Voyage.Domain;
using Xunit;

namespace Logistics.IntegrationTests;

public sealed class CrashRecoveryTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("LOGISTICS_TEST_CONNECTION_STRING")
        ?? "Server=192.168.33.37,1433;Database=Logistics;User Id=sa;Password=Mehd!K@1;TrustServerCertificate=True;Encrypt=False";

    [Fact]
    public async Task Crash_immediately_after_reservation_recovers_without_double_reserving()
    {
        var options = new DbContextOptionsBuilder<LogisticsDbContext>()
            .UseSqlServer(ConnectionString)
            .Options;

        var bookingId = "B001";
        var oldVoyageId = "V100";
        var targetVoyageId = "V200";
        var processId = Guid.NewGuid();
        var disruptionId = Guid.NewGuid();

        await using (var setup = new LogisticsDbContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
            // Keep this test isolated from previous runs.
            await setup.Database.ExecuteSqlRawAsync("DELETE FROM dbo.CapacityReservations; DELETE FROM dbo.VoyageAssignments; DELETE FROM dbo.ReplanningProcesses; DELETE FROM dbo.Bookings; DELETE FROM dbo.Voyages;");

            setup.Voyages.Add(new Voyage.Domain.Voyage(oldVoyageId, 10));
            setup.Voyages.Add(new Voyage.Domain.Voyage(targetVoyageId, 1));
            setup.Bookings.Add(new Booking.Domain.Booking(bookingId, 1));
            await setup.SaveChangesAsync();

            var booking = await setup.Bookings.Include(x => x.Assignments).SingleAsync(x => x.Id == bookingId);
            booking.Reassign(oldVoyageId, DateTimeOffset.UtcNow);
            await setup.SaveChangesAsync();

            setup.ReplanningProcesses.Add(new ReplanningProcess(processId, bookingId, disruptionId));
            await setup.SaveChangesAsync();
        }

        // Move the process to AlternativeSelected just as the real worker would.
        await using (var db = new LogisticsDbContext(options))
        {
            var process = await db.ReplanningProcesses.SingleAsync(x => x.Id == processId);
            process.ConfirmEligibility();
            process.SelectAlternative(targetVoyageId);
            await db.SaveChangesAsync();
        }

        await using (var crashDb = new LogisticsDbContext(options))
        {
            var repo = new ReplanningProcessRepository(crashDb);
            var bookings = new BookingGateway(crashDb);
            var voyages = new VoyageGateway(crashDb);
            var crashInjector = new CrashAfterReservationInjector();
            var orchestrator = new ReplanningOrchestrator(
                repo, bookings, voyages, new SystemClock(), new ExponentialRetryPolicy(), crashInjector,
                NullLogger<ReplanningOrchestrator>.Instance);

            await Assert.ThrowsAsync<SimulatedCrashException>(() => orchestrator.ExecuteAsync(processId, CancellationToken.None));
        }

        // Crash happened after reservation commit but before process state commit.
        await using (var afterCrash = new LogisticsDbContext(options))
        {
            var process = await afterCrash.ReplanningProcesses.SingleAsync(x => x.Id == processId);
            Assert.Equal(ReplanningState.AlternativeSelected, process.State);

            var reservationCount = await afterCrash.CapacityReservations
                .CountAsync(x => x.ProcessId == processId && x.State == ReservationState.Active);
            Assert.Equal(1, reservationCount);

            var target = await afterCrash.Voyages.SingleAsync(x => x.Id == targetVoyageId);
            Assert.Equal(1, target.ReservedCapacity);
        }

        // Simulate application restart: a new scope/repository/orchestrator resumes the durable process.
        await using (var recoveryDb = new LogisticsDbContext(options))
        {
            var repo = new ReplanningProcessRepository(recoveryDb);
            var bookings = new BookingGateway(recoveryDb);
            var voyages = new VoyageGateway(recoveryDb);
            var orchestrator = new ReplanningOrchestrator(
                repo, bookings, voyages, new SystemClock(), new ExponentialRetryPolicy(),
                new NoopWorkflowFailureInjector(), NullLogger<ReplanningOrchestrator>.Instance);

            await orchestrator.ExecuteAsync(processId, CancellationToken.None);
            // One invocation advances one durable step. Continue until terminal.
            for (var i = 0; i < 10; i++)
            {
                var state = await recoveryDb.ReplanningProcesses.AsNoTracking()
                    .Where(x => x.Id == processId).Select(x => x.State).SingleAsync();
                if (state == ReplanningState.Completed) break;
                await orchestrator.ExecuteAsync(processId, CancellationToken.None);
            }
        }

        await using (var verify = new LogisticsDbContext(options))
        {
            var process = await verify.ReplanningProcesses.AsNoTracking().SingleAsync(x => x.Id == processId);
            Assert.Equal(ReplanningState.Completed, process.State);

            var target = await verify.Voyages.AsNoTracking().SingleAsync(x => x.Id == targetVoyageId);
            Assert.Equal(1, target.ReservedCapacity);

            var activeTargetReservations = await verify.CapacityReservations
                .CountAsync(x => x.ProcessId == processId && x.VoyageId == targetVoyageId && x.State == ReservationState.Active);
            Assert.Equal(1, activeTargetReservations);

            var booking = await verify.Bookings.Include(x => x.Assignments).AsNoTracking().SingleAsync(x => x.Id == bookingId);
            Assert.Equal(targetVoyageId, booking.ActiveAssignment!.VoyageId);
        }
    }

    private sealed class CrashAfterReservationInjector : IWorkflowFailureInjector
    {
        public Task AfterCapacityReservationAsync(Guid processId, string voyageId, CancellationToken ct)
            => throw new SimulatedCrashException(processId);
    }
}
