using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Logistics.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Replanning.Domain;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<LogisticsDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("SqlServer"),
    b=>b.MigrationsAssembly(typeof(LogisticsDbContext).GetTypeInfo().Assembly.GetName().Name)));

builder.Services.AddLogisticsInfrastructure();
builder.Services.AddHostedService<ReplanningWorker>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.Use(async (context, next) =>
{
    var correlationId = context.Request.Headers["X-Correlation-Id"].FirstOrDefault()
                        ?? Guid.NewGuid().ToString("N");
    context.Response.Headers["X-Correlation-Id"] = correlationId;
    using (app.Logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
    {
        await next();
    }
});

app.UseSwagger();
app.UseSwaggerUI();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/api/voyages", async (CreateVoyageRequest request, LogisticsDbContext db) =>
{
    if (!ApiValidation.IsBusinessKeyValid(request.VoyageId))
        return Results.BadRequest(new { code = "INVALID_VOYAGE_ID", message = "VoyageId must be 1-10 ASCII characters." });

    if (request.TotalCapacity <= 0)
        return Results.BadRequest(new { code = "INVALID_CAPACITY", message = "TotalCapacity must be greater than zero." });

    if (await db.Voyages.AnyAsync(x => x.Id == request.VoyageId))
        return Results.Conflict(new { code = "VOYAGE_ALREADY_EXISTS", voyageId = request.VoyageId });

    var voyage = new Voyage.Domain.Voyage(request.VoyageId, request.TotalCapacity);
    db.Voyages.Add(voyage);

    try
    {
        await db.SaveChangesAsync();
    }
    catch (DbUpdateException)
    {
        return Results.Conflict(new { code = "VOYAGE_ALREADY_EXISTS", voyageId = request.VoyageId });
    }

    return Results.Created($"/api/voyages/{voyage.Id}", new
    {
        voyage.Id,
        state = voyage.State.ToString(),
        voyage.TotalCapacity,
        voyage.ReservedCapacity,
        voyage.Version
    });
});

app.MapPost("/api/bookings", async (CreateBookingRequest request, LogisticsDbContext db) =>
{
    if (!ApiValidation.IsBusinessKeyValid(request.BookingId))
        return Results.BadRequest(new { code = "INVALID_BOOKING_ID", message = "BookingId must be 1-10 ASCII characters." });

    if (request.RequestedTeu <= 0)
        return Results.BadRequest(new { code = "INVALID_REQUESTED_TEU", message = "RequestedTeu must be greater than zero." });

    if (await db.Bookings.AnyAsync(x => x.Id == request.BookingId))
        return Results.Conflict(new { code = "BOOKING_ALREADY_EXISTS", bookingId = request.BookingId });

    var booking = new Booking.Domain.Booking(request.BookingId, request.RequestedTeu);
    db.Bookings.Add(booking);

    try
    {
        await db.SaveChangesAsync();
    }
    catch (DbUpdateException)
    {
        return Results.Conflict(new { code = "BOOKING_ALREADY_EXISTS", bookingId = request.BookingId });
    }

    return Results.Created($"/api/bookings/{booking.Id}", new
    {
        booking.Id,
        state = booking.State.ToString(),
        booking.RequestedTeu,
        activeVoyageId = booking.ActiveAssignment?.VoyageId,
        booking.Version
    });
});

app.MapPost("/api/voyages/{voyageId}/disruptions",
    async (string voyageId, LogisticsDbContext db) =>
{
    var voyage = await db.Voyages.SingleOrDefaultAsync(x => x.Id == voyageId);
    if (voyage is null) return Results.NotFound(new { code = "VOYAGE_NOT_FOUND", voyageId });

    voyage.Disrupt();

    var messageId = Guid.NewGuid();
    var message = new Logistics.Infrastructure.Consumers.VoyageDisruptedMessage(
        messageId, voyageId, DateTimeOffset.UtcNow, 1);

    db.OutboxMessages.Add(OutboxMessage.Create(
        "VoyageDisrupted",
        messageId,
        JsonSerializer.Serialize(message)));

    await db.SaveChangesAsync();
    return Results.Accepted($"/api/replanning/events/{messageId}", new { messageId, voyageId, state = voyage.State.ToString() });
});

app.MapPost("/api/bookings/{bookingId}/replanning",
    async (string bookingId, StartReplanningRequest request, HttpRequest http, LogisticsDbContext db) =>
{
    var key = http.Headers["Idempotency-Key"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(key))
        return Results.BadRequest(new { code = "IDEMPOTENCY_KEY_REQUIRED" });

    var scope = $"POST:/api/bookings/{bookingId}/replanning";
    var payload = JsonSerializer.Serialize(request);
    var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));

    var existing = await db.IdempotencyRecords
        .SingleOrDefaultAsync(x => x.Scope == scope && x.Key == key);

    if (existing is not null)
    {
        if (!string.Equals(existing.PayloadHash, hash, StringComparison.Ordinal))
            return Results.Conflict(new { code = "IDEMPOTENCY_KEY_PAYLOAD_MISMATCH" });

        return Results.Accepted($"/api/replanning/{existing.ProcessId}",
            new { processId = existing.ProcessId, state = existing.Status });
    }

    var process = new ReplanningProcess(Guid.NewGuid(), bookingId, request.DisruptionId);
    db.ReplanningProcesses.Add(process);
    db.IdempotencyRecords.Add(IdempotencyRecord.Create(scope, key, hash, process.Id));

    try
    {
        await db.SaveChangesAsync();
    }
    catch (DbUpdateException)
    {
        // Concurrent duplicate key: read the winner and return it.
        var winner = await db.IdempotencyRecords
            .SingleAsync(x => x.Scope == scope && x.Key == key);

        return Results.Accepted($"/api/replanning/{winner.ProcessId}",
            new { processId = winner.ProcessId, state = winner.Status });
    }

    return Results.Accepted($"/api/replanning/{process.Id}",
        new { processId = process.Id, state = process.State.ToString() });
});

app.MapPost("/api/replanning/{processId:guid}/retry",
    async (Guid processId, LogisticsDbContext db) =>
{
    var process = await db.ReplanningProcesses.FindAsync(processId);
    if (process is null) return Results.NotFound();

    if (process.State == ReplanningState.Completed)
        return Results.Conflict(new { code = "PROCESS_ALREADY_COMPLETED" });

    process.ScheduleRetry("Manual retry requested.", DateTimeOffset.UtcNow);
    await db.SaveChangesAsync();

    return Results.Accepted($"/api/replanning/{processId}",
        new { processId, state = process.State.ToString() });
});

app.MapGet("/api/replanning/{processId:guid}",
    async (Guid processId, LogisticsDbContext db) =>
{
    var process = await db.ReplanningProcesses.FindAsync(processId);
    if (process is null) return Results.NotFound();

    return Results.Ok(new
    {
        process.Id,
        process.BookingId,
        process.DisruptionId,
        state = process.State.ToString(),
        process.CurrentVoyageId,
        process.TargetVoyageId,
        process.RetryCount,
        process.NextAttemptAt,
        process.LastError,
        process.CreatedAt,
        process.UpdatedAt,
        process.CompletedAt
    });
});

app.Run();

public sealed record CreateVoyageRequest(string VoyageId, int TotalCapacity);
public sealed record CreateBookingRequest(string BookingId, int RequestedTeu);
public sealed record StartReplanningRequest(Guid DisruptionId, string Reason, string? RequestedAlternativeVoyageId);

public sealed class ReplanningWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<ReplanningWorker> logger) : BackgroundService
{
    private readonly string _owner = $"worker-{Environment.ProcessId}-{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var repository = scope.ServiceProvider.GetRequiredService<Replanning.Application.IReplanningProcessRepository>();
                var orchestrator = scope.ServiceProvider.GetRequiredService<Replanning.Application.ReplanningOrchestrator>();
                var now = DateTimeOffset.UtcNow;
                var processes = await repository.GetDueAsync(now, 20, stoppingToken);

                foreach (var candidate in processes)
                {
                    var leaseUntil = now.AddSeconds(30);
                    if (!await repository.TryClaimAsync(candidate.Id, _owner, leaseUntil, candidate.Version, stoppingToken))
                        continue;

                    try
                    {
                        await orchestrator.ExecuteAsync(candidate.Id, stoppingToken);
                    }
                    catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                    {
                        logger.LogError(ex, "Unexpected worker error for process {ProcessId}.", candidate.Id);
                    }
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Replanning worker iteration failed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }
}

internal static class ApiValidation
{
    public static bool IsBusinessKeyValid(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 10 &&
        value.All(c => c <= 127 && !char.IsControl(c));
}
