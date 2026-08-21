using Booking.Domain;
using Microsoft.EntityFrameworkCore;
using Replanning.Application;
using Replanning.Domain;
using System.Data;
using Voyage.Domain;

namespace Logistics.Infrastructure;

public sealed class ReplanningProcessRepository(LogisticsDbContext db) : IReplanningProcessRepository
{
    public Task<ReplanningProcess?> GetAsync(Guid id, CancellationToken ct) => db.ReplanningProcesses.SingleOrDefaultAsync(x => x.Id == id, ct);

    public async Task<bool> TryClaimAsync(Guid id, string owner, DateTimeOffset leaseUntil, long expectedVersion, CancellationToken ct)
    {
        var rows = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE dbo.ReplanningProcesses
            SET LeaseOwner = {owner}, LeaseUntil = {leaseUntil}, Version = Version + 1, UpdatedAt = SYSUTCDATETIME()
            WHERE Id = {id}
              AND Version = {expectedVersion}
              AND (LeaseUntil IS NULL OR LeaseUntil < SYSUTCDATETIME() OR LeaseOwner = {owner});
            """, ct);
        if (rows == 1) db.ChangeTracker.Clear();
        return rows == 1;
    }

    public async Task SaveAsync(ReplanningProcess process, CancellationToken ct)
    {
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<ReplanningProcess>> GetDueAsync(DateTimeOffset now, int batchSize, CancellationToken ct)
    {
        return await db.ReplanningProcesses
            .Where(x => x.State != ReplanningState.Completed && x.State != ReplanningState.ManualReview)
            .Where(x => x.NextAttemptAt == null || x.NextAttemptAt <= now)
            .Where(x => x.LeaseUntil == null || x.LeaseUntil <= now)
            .OrderBy(x => x.UpdatedAt)
            .Take(batchSize)
            .ToListAsync(ct);
    }
}

public sealed class BookingGateway(LogisticsDbContext db) : IBookingGateway
{
    public async Task<BookingSnapshot?> GetAsync(string bookingId, CancellationToken ct)
    {
        var b = await db.Bookings.Include(x => x.Assignments).SingleOrDefaultAsync(x => x.Id == bookingId, ct);
        return b is null ? null : new BookingSnapshot(b.Id, b.RequestedTeu, b.ActiveAssignment?.VoyageId, b.State == BookingState.Active);
    }

    public async Task ReassignAsync(string bookingId, string voyageId, DateTimeOffset now, CancellationToken ct)
    {
        var b = await db.Bookings.Include(x => x.Assignments).SingleOrDefaultAsync(x => x.Id == bookingId, ct)
            ?? throw new InvalidOperationException("Booking not found.");
        b.Reassign(voyageId, now);
        await db.SaveChangesAsync(ct);
    }
}

public sealed class VoyageGateway(LogisticsDbContext db) : IVoyageGateway
{
    public async Task<IReadOnlyList<VoyageCandidate>> FindAlternativesAsync(string bookingId, string? preferredVoyageId, CancellationToken ct)
    {
        var query = db.Voyages.Where(x => x.State == VoyageState.Available && x.ReservedCapacity < x.TotalCapacity);
        if (!string.IsNullOrWhiteSpace(preferredVoyageId))
            query = query.Where(x => x.Id != preferredVoyageId);
        return await query
            .OrderByDescending(x => x.TotalCapacity - x.ReservedCapacity)
            .Select(x => new VoyageCandidate(x.Id, x.TotalCapacity - x.ReservedCapacity))
            .Take(20)
            .ToListAsync(ct);
    }

    public async Task<ReservationResult> ReserveAsync(string voyageId, string bookingId, Guid processId, int units, CancellationToken ct)
    {
        var existing = await db.CapacityReservations.SingleOrDefaultAsync(x => x.ProcessId == processId && x.VoyageId == voyageId, ct);
        if (existing is not null && existing.State == ReservationState.Active)
            return new ReservationResult(existing.Id, false, true);

        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE dbo.Voyages
            SET ReservedCapacity = ReservedCapacity + {units}, Version = Version + 1
            WHERE Id = {voyageId}
              AND State = 'Available'
              AND ReservedCapacity + {units} <= TotalCapacity;
            """, ct);
        if (affected != 1)
            throw new CapacityUnavailableException(voyageId);

        var reservation = new CapacityReservation(Guid.NewGuid(), bookingId, voyageId, processId, units);
        db.CapacityReservations.Add(reservation);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new ReservationResult(reservation.Id, true, false);
    }

    public async Task ReleaseForBookingVoyageAsync(string bookingId, string voyageId, CancellationToken ct)
    {
        var reservation = await db.CapacityReservations
            .Where(x => x.BookingId == bookingId && x.VoyageId == voyageId && x.State == ReservationState.Active)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (reservation is null) return;
        await ReleaseAsync(reservation.Id, ct);
    }

    public async Task ReleaseAsync(Guid reservationId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var reservation = await db.CapacityReservations.SingleOrDefaultAsync(x => x.Id == reservationId, ct);
        if (reservation is null || reservation.State == ReservationState.Released)
        {
            await tx.CommitAsync(ct);
            return;
        }
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE dbo.Voyages
            SET ReservedCapacity = ReservedCapacity - {reservation.Units}, Version = Version + 1
            WHERE Id = {reservation.VoyageId}
              AND ReservedCapacity >= {reservation.Units};
            """, ct);
        if (affected != 1) throw new InvalidOperationException("Capacity release invariant failed.");
        reservation.Release(DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}

public sealed class CapacityUnavailableException(string voyageId) : Exception($"Capacity unavailable for voyage {voyageId}.");

public sealed class SystemClock : IReplanningClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public sealed class ExponentialRetryPolicy : IRetryPolicy
{
    public DateTimeOffset NextAttempt(int retryCount, DateTimeOffset now)
    {
        var seconds = Math.Min(300, Math.Pow(2, Math.Min(retryCount, 8)));
        return now.AddSeconds(seconds);
    }
}
