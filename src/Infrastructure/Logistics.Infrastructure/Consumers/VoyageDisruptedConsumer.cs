using Booking.Domain;
using Microsoft.EntityFrameworkCore;
using Replanning.Domain;

namespace Logistics.Infrastructure.Consumers;

public sealed record VoyageDisruptedMessage(
    Guid MessageId,
    string VoyageId,
    DateTimeOffset OccurredAt,
    long Sequence);

/// <summary>
/// At-least-once consumer for VoyageDisrupted.
/// Inbox and process creation happen in the same database transaction.
/// If the broker redelivers the message, ProcessedMessages prevents a second effect.
/// If an old/out-of-order disruption arrives after a booking has already left the voyage,
/// there is no active assignment and therefore no new replanning process is created.
/// </summary>
public sealed class VoyageDisruptedConsumer(LogisticsDbContext db)
{
    private const string ConsumerName = "Replanning.VoyageDisrupted";

    public async Task<int> HandleAsync(VoyageDisruptedMessage message, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var duplicate = await db.ProcessedMessages
            .AsNoTracking()
            .AnyAsync(x => x.ConsumerName == ConsumerName && x.MessageId == message.MessageId, ct);

        if (duplicate)
        {
            await tx.CommitAsync(ct);
            return 0;
        }

        // Inbox row is written in the same transaction as the business effect.
        db.ProcessedMessages.Add(new ProcessedMessage(ConsumerName, message.MessageId));

        // The current assignment is the source of truth for whether this disruption
        // still affects a booking. This makes late/out-of-order messages harmless.
        var affectedBookings = await db.Bookings
            .Where(b => b.State == BookingState.Active)
            .Select(b => new
            {
                b.Id,
                CurrentVoyageId = b.Assignments
                    .Where(a => a.State == AssignmentState.Active)
                    .OrderByDescending(a => a.Sequence)
                    .Select(a => a.VoyageId)
                    .FirstOrDefault()
            })
            .Where(x => x.CurrentVoyageId == message.VoyageId)
            .ToListAsync(ct);

        var created = 0;
        foreach (var booking in affectedBookings)
        {
            // Business idempotency: one process per disruption + booking.
            var exists = await db.ReplanningProcesses
                .AnyAsync(p => p.DisruptionId == message.MessageId && p.BookingId == booking.Id, ct);

            if (exists)
                continue;

            db.ReplanningProcesses.Add(new ReplanningProcess(
                Guid.NewGuid(),
                booking.Id,
                message.MessageId,
                booking.CurrentVoyageId));
            created++;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return created;
    }
}
