using Microsoft.EntityFrameworkCore;
using Replanning.Domain;
using Voyage.Domain;

namespace Logistics.Infrastructure;

public sealed class LogisticsDbContext(DbContextOptions<LogisticsDbContext> options) : DbContext(options)
{
    public DbSet<Booking.Domain.Booking> Bookings => Set<Booking.Domain.Booking>();
    public DbSet<Voyage.Domain.Voyage> Voyages => Set<Voyage.Domain.Voyage>();
    public DbSet<CapacityReservation> CapacityReservations => Set<CapacityReservation>();
    public DbSet<ReplanningProcess> ReplanningProcesses => Set<ReplanningProcess>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // ============================================================
        // Bookings
        // ============================================================
        b.Entity<Booking.Domain.Booking>(e =>
        {
            e.ToTable("Bookings", "dbo", tb =>
            {
                tb.HasCheckConstraint(
                    "CK_Bookings_RequestedTeu",
                    "[RequestedTeu] > 0");
            });

            e.HasKey(x => x.Id)
                .HasName("PK_Bookings");

            e.Property(x => x.Id)
                .HasColumnType("varchar(10)")
                .IsRequired();

            e.Property(x => x.RequestedTeu)
                .HasColumnType("int")
                .IsRequired();

            e.Property(x => x.State)
                .HasColumnType("varchar(32)")
                .HasConversion<string>()
                .IsRequired();

            e.Property(x => x.Version)
                .HasColumnType("bigint")
                .IsRequired()
                .IsConcurrencyToken()
                .HasDefaultValue(0L);


            // --------------------------------------------------------
            // Booking.Assignments -> VoyageAssignments
            // --------------------------------------------------------
            e.OwnsMany(x => x.Assignments, a =>
            {
                a.ToTable("VoyageAssignments", "dbo", tb =>
                {
                    // SQL:
                    // CONSTRAINT FK_VoyageAssignments_Bookings
                    // FOREIGN KEY (BookingId)
                    // REFERENCES dbo.Bookings(Id)
                });

                a.WithOwner()
                    .HasForeignKey("BookingId")
                    .HasConstraintName("FK_VoyageAssignments_Bookings");

                a.Property<Guid>("Id")
                    .HasColumnType("uniqueidentifier")
                    .IsRequired();

                a.HasKey("Id")
                    .HasName("PK_VoyageAssignments");

                a.Property<string>("BookingId")
                    .HasColumnType("varchar(10)")
                    .IsRequired();

                a.Property(x => x.VoyageId)
                    .HasColumnType("varchar(10)")
                    .IsRequired();

                a.Property(x => x.State)
                    .HasColumnType("varchar(32)")
                    .HasConversion<string>()
                    .IsRequired();

                a.Property(x => x.Sequence)
                    .HasColumnType("bigint")
                    .IsRequired();

                a.Property(x => x.EffectiveAt)
                    .HasColumnType("datetimeoffset")
                    .IsRequired();

                // UX_VoyageAssignments_Active_Booking
                a.HasIndex("BookingId")
                    .IsUnique()
                    .HasDatabaseName("UX_VoyageAssignments_Active_Booking")
                    .HasFilter("[State] = 'Active'");

                // IX_VoyageAssignments_Voyage_State
                a.HasIndex(x => new
                {
                    x.VoyageId,
                    x.State
                })
                .HasDatabaseName("IX_VoyageAssignments_Voyage_State");
            });
        });


        // ============================================================
        // Voyages
        // ============================================================
        b.Entity<Voyage.Domain.Voyage>(e =>
        {
            e.ToTable("Voyages", "dbo", tb =>
            {
                tb.HasCheckConstraint(
                    "CK_Voyages_Capacity",
                    "[TotalCapacity] > 0 AND [ReservedCapacity] >= 0 AND [ReservedCapacity] <= [TotalCapacity]");
            });

            e.HasKey(x => x.Id)
                .HasName("PK_Voyages");

            e.Property(x => x.Id)
                .HasColumnType("varchar(10)")
                .IsRequired();

            e.Property(x => x.State)
                .HasColumnType("varchar(32)")
                .HasConversion<string>()
                .IsRequired();

            e.Property(x => x.TotalCapacity)
                .HasColumnType("int")
                .IsRequired();

            e.Property(x => x.ReservedCapacity)
                .HasColumnType("int")
                .IsRequired()
                .HasDefaultValue(0);

            e.Property(x => x.Version)
                .HasColumnType("bigint")
                .IsRequired()
                .IsConcurrencyToken()
                .HasDefaultValue(0L);
        });


        // ============================================================
        // CapacityReservations
        // ============================================================
        b.Entity<CapacityReservation>(e =>
        {
            e.ToTable("CapacityReservations", "dbo", tb =>
            {
                tb.HasCheckConstraint(
                    "CK_CapacityReservations_Units",
                    "[Units] > 0");
            });

            e.HasKey(x => x.Id)
                .HasName("PK_CapacityReservations");

            e.Property(x => x.Id)
                .HasColumnType("uniqueidentifier")
                .IsRequired();

            e.Property(x => x.BookingId)
                .HasColumnType("varchar(10)")
                .IsRequired();

            e.Property(x => x.VoyageId)
                .HasColumnType("varchar(10)")
                .IsRequired();

            e.Property(x => x.ProcessId)
                .HasColumnType("uniqueidentifier")
                .IsRequired();

            e.Property(x => x.Units)
                .HasColumnType("int")
                .IsRequired();

            e.Property(x => x.State)
                .HasColumnType("varchar(32)")
                .HasConversion<string>()
                .IsRequired();

            e.Property(x => x.CreatedAt)
                .HasColumnType("datetimeoffset")
                .IsRequired();

            e.Property(x => x.ReleasedAt)
                .HasColumnType("datetimeoffset");

            // FK_CapacityReservations_Bookings
            e.HasOne<Booking.Domain.Booking>()
                .WithMany()
                .HasForeignKey(x => x.BookingId)
                .HasPrincipalKey(x => x.Id)
                .HasConstraintName("FK_CapacityReservations_Bookings");

            // FK_CapacityReservations_Voyages
            e.HasOne<Voyage.Domain.Voyage>()
                .WithMany()
                .HasForeignKey(x => x.VoyageId)
                .HasPrincipalKey(x => x.Id)
                .HasConstraintName("FK_CapacityReservations_Voyages");

            // UX_CapacityReservations_Process_Voyage
            e.HasIndex(x => new
            {
                x.ProcessId,
                x.VoyageId
            })
            .IsUnique()
            .HasDatabaseName("UX_CapacityReservations_Process_Voyage");

            // IX_CapacityReservations_Voyage_State
            e.HasIndex(x => new
            {
                x.VoyageId,
                x.State
            })
            .HasDatabaseName("IX_CapacityReservations_Voyage_State");
        });


        // ============================================================
        // ReplanningProcesses
        // ============================================================
        b.Entity<ReplanningProcess>(e =>
        {
            e.ToTable("ReplanningProcesses", "dbo");

            e.HasKey(x => x.Id)
                .HasName("PK_ReplanningProcesses");

            e.Property(x => x.Id)
                .HasColumnType("uniqueidentifier")
                .IsRequired();

            e.Property(x => x.BookingId)
                .HasColumnType("varchar(10)")
                .IsRequired();

            e.Property(x => x.DisruptionId)
                .HasColumnType("uniqueidentifier")
                .IsRequired();

            e.Property(x => x.State)
                .HasColumnType("varchar(64)")
                .HasConversion<string>()
                .IsRequired();

            e.Property(x => x.CurrentVoyageId)
                .HasColumnType("varchar(10)");

            e.Property(x => x.PreviousVoyageId)
                .HasColumnType("varchar(10)");

            e.Property(x => x.TargetVoyageId)
                .HasColumnType("varchar(10)");

            e.Property(x => x.RetryCount)
                .HasColumnType("int")
                .IsRequired()
                .HasDefaultValue(0);

            e.Property(x => x.NextAttemptAt)
                .HasColumnType("datetimeoffset");

            e.Property(x => x.LastError)
                .HasColumnType("nvarchar(2000)");

            e.Property(x => x.Version)
                .HasColumnType("bigint")
                .IsRequired()
                .IsConcurrencyToken()
                .HasDefaultValue(0L);

            e.Property(x => x.CreatedAt)
                .HasColumnType("datetimeoffset")
                .IsRequired();

            e.Property(x => x.UpdatedAt)
                .HasColumnType("datetimeoffset")
                .IsRequired();

            e.Property(x => x.CompletedAt)
                .HasColumnType("datetimeoffset");

            e.Property(x => x.LeaseOwner)
                .HasColumnType("varchar(128)");

            e.Property(x => x.LeaseUntil)
                .HasColumnType("datetimeoffset");

            // FK_Replanning_Bookings
            e.HasOne<Booking.Domain.Booking>()
                .WithMany()
                .HasForeignKey(x => x.BookingId)
                .HasPrincipalKey(x => x.Id)
                .HasConstraintName("FK_Replanning_Bookings");

            // UX_Replanning_Disruption_Booking
            e.HasIndex(x => new
            {
                x.DisruptionId,
                x.BookingId
            })
            .IsUnique()
            .HasDatabaseName("UX_Replanning_Disruption_Booking");

            // IX_Replanning_Due
            e.HasIndex(x => new
            {
                x.State,
                x.NextAttemptAt
            })
            .HasDatabaseName("IX_Replanning_Due");
        });


        // ============================================================
        // OutboxMessages
        // ============================================================
        b.Entity<OutboxMessage>(e =>
        {
            e.ToTable("OutboxMessages", "dbo");

            e.HasKey(x => x.Id)
                .HasName("PK_OutboxMessages");

            e.Property(x => x.Id)
                .HasColumnType("uniqueidentifier")
                .IsRequired();

            e.Property(x => x.MessageType)
                .HasColumnType("varchar(256)")
                .IsRequired();

            e.Property(x => x.AggregateId)
                .HasColumnType("uniqueidentifier")
                .IsRequired();

            e.Property(x => x.Payload)
                .HasColumnType("nvarchar(max)")
                .IsRequired();

            e.Property(x => x.OccurredAt)
                .HasColumnType("datetimeoffset")
                .IsRequired();

            e.Property(x => x.Status)
                .HasColumnType("varchar(32)")
                .HasConversion<string>()
                .IsRequired();

            e.Property(x => x.Attempts)
                .HasColumnType("int")
                .IsRequired()
                .HasDefaultValue(0);

            e.Property(x => x.NextAttemptAt)
                .HasColumnType("datetimeoffset")
                .IsRequired();

            e.Property(x => x.PublishedAt)
                .HasColumnType("datetimeoffset");

            e.Property(x => x.LastError)
                .HasColumnType("nvarchar(2000)");

            e.Property(x => x.LeaseOwner)
                .HasColumnType("varchar(128)");

            e.Property(x => x.LeaseUntil)
                .HasColumnType("datetimeoffset");

            // IX_Outbox_Due
            e.HasIndex(x => new
            {
                x.Status,
                x.NextAttemptAt
            })
            .HasDatabaseName("IX_Outbox_Due");

            // IX_Outbox_Lease
            e.HasIndex(x => new
            {
                x.Status,
                x.LeaseUntil
            })
            .HasDatabaseName("IX_Outbox_Lease");
        });


        // ============================================================
        // IdempotencyRecords
        // ============================================================
        b.Entity<IdempotencyRecord>(e =>
        {
            e.ToTable("IdempotencyRecords", "dbo");

            e.HasKey(x => x.Id)
                .HasName("PK_IdempotencyRecords");

            e.Property(x => x.Id)
                .HasColumnType("uniqueidentifier")
                .IsRequired();

            e.Property(x => x.Scope)
                .HasColumnType("varchar(512)")
                .IsRequired();

            e.Property(x => x.Key)
                .HasColumnName("Key")
                .HasColumnType("varchar(256)")
                .IsRequired();

            e.Property(x => x.PayloadHash)
                .HasColumnType("varchar(128)")
                .IsRequired();

            e.Property(x => x.ProcessId)
                .HasColumnType("uniqueidentifier")
                .IsRequired();

            e.Property(x => x.Status)
                .HasColumnType("varchar(32)")
                .HasConversion<string>()
                .IsRequired();

            e.Property(x => x.ResponsePayload)
                .HasColumnType("nvarchar(max)");

            e.Property(x => x.CreatedAt)
                .HasColumnType("datetimeoffset")
                .IsRequired();

            e.Property(x => x.ExpiresAt)
                .HasColumnType("datetimeoffset")
                .IsRequired();

            // UX_Idempotency_Scope_Key
            e.HasIndex(x => new
            {
                x.Scope,
                x.Key
            })
            .IsUnique()
            .HasDatabaseName("UX_Idempotency_Scope_Key");
        });


        // ============================================================
        // ProcessedMessages
        // ============================================================
        b.Entity<ProcessedMessage>(e =>
        {
            e.ToTable("ProcessedMessages", "dbo");

            e.HasKey(x => new
            {
                x.ConsumerName,
                x.MessageId
            })
            .HasName("PK_ProcessedMessages");

            e.Property(x => x.ConsumerName)
                .HasColumnType("varchar(256)")
                .IsRequired();

            e.Property(x => x.MessageId)
                .HasColumnType("uniqueidentifier")
                .IsRequired();

            e.Property(x => x.ProcessedAt)
                .HasColumnType("datetimeoffset")
                .IsRequired();
        });
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        return await base.SaveChangesAsync(cancellationToken);
    }
}

public enum OutboxStatus { Pending = 1, Processing = 2, Published = 3, Failed = 4 }

public sealed class OutboxMessage
{
    private OutboxMessage() { }
    public Guid Id { get; private set; } = Guid.NewGuid();
    public string MessageType { get; private set; } = "";
    public Guid AggregateId { get; private set; }
    public string Payload { get; private set; } = "";
    public DateTimeOffset OccurredAt { get; private set; }
    public OutboxStatus Status { get; private set; }
    public int Attempts { get; private set; }
    public DateTimeOffset NextAttemptAt { get; private set; }
    public DateTimeOffset? PublishedAt { get; private set; }
    public string? LastError { get; private set; }
    public string? LeaseOwner { get; private set; }
    public DateTimeOffset? LeaseUntil { get; private set; }

    public static OutboxMessage Create(string type, Guid aggregateId, string payload) =>
        new()
        {
            MessageType = type,
            AggregateId = aggregateId,
            Payload = payload,
            OccurredAt = DateTimeOffset.UtcNow,
            NextAttemptAt = DateTimeOffset.UtcNow,
            Status = OutboxStatus.Pending
        };
}

public sealed class IdempotencyRecord
{
    private IdempotencyRecord() { }
    public Guid Id { get; private set; } = Guid.NewGuid();
    public string Scope { get; private set; } = "";
    public string Key { get; private set; } = "";
    public string PayloadHash { get; private set; } = "";
    public Guid ProcessId { get; private set; }
    public string Status { get; private set; } = "";
    public string? ResponsePayload { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }

    public static IdempotencyRecord Create(string scope, string key, string payloadHash, Guid processId) =>
        new()
        {
            Scope = scope,
            Key = key,
            PayloadHash = payloadHash,
            ProcessId = processId,
            Status = "Accepted",
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(24)
        };
}

public sealed class ProcessedMessage
{
    private ProcessedMessage() { }
    public string ConsumerName { get; private set; } = "";
    public Guid MessageId { get; private set; }
    public DateTimeOffset ProcessedAt { get; private set; } = DateTimeOffset.UtcNow;

    public ProcessedMessage(string consumerName, Guid messageId)
    {
        ConsumerName = consumerName;
        MessageId = messageId;
    }
}
