using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Logistics.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "dbo");

            migrationBuilder.CreateTable(
                name: "Bookings",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<string>(type: "varchar(10)", nullable: false),
                    RequestedTeu = table.Column<int>(type: "int", nullable: false),
                    State = table.Column<string>(type: "varchar(32)", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Bookings", x => x.Id);
                    table.CheckConstraint("CK_Bookings_RequestedTeu", "[RequestedTeu] > 0");
                });

            migrationBuilder.CreateTable(
                name: "IdempotencyRecords",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Scope = table.Column<string>(type: "varchar(512)", nullable: false),
                    Key = table.Column<string>(type: "varchar(256)", nullable: false),
                    PayloadHash = table.Column<string>(type: "varchar(128)", nullable: false),
                    ProcessId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<string>(type: "varchar(32)", nullable: false),
                    ResponsePayload = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdempotencyRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OutboxMessages",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MessageType = table.Column<string>(type: "varchar(256)", nullable: false),
                    AggregateId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Payload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Status = table.Column<string>(type: "varchar(32)", nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(2000)", nullable: true),
                    LeaseOwner = table.Column<string>(type: "varchar(128)", nullable: true),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProcessedMessages",
                schema: "dbo",
                columns: table => new
                {
                    ConsumerName = table.Column<string>(type: "varchar(256)", nullable: false),
                    MessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProcessedMessages", x => new { x.ConsumerName, x.MessageId });
                });

            migrationBuilder.CreateTable(
                name: "Voyages",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<string>(type: "varchar(10)", nullable: false),
                    State = table.Column<string>(type: "varchar(32)", nullable: false),
                    TotalCapacity = table.Column<int>(type: "int", nullable: false),
                    ReservedCapacity = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    Version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Voyages", x => x.Id);
                    table.CheckConstraint("CK_Voyages_Capacity", "[TotalCapacity] > 0 AND [ReservedCapacity] >= 0 AND [ReservedCapacity] <= [TotalCapacity]");
                });

            migrationBuilder.CreateTable(
                name: "ReplanningProcesses",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BookingId = table.Column<string>(type: "varchar(10)", nullable: false),
                    DisruptionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    State = table.Column<string>(type: "varchar(64)", nullable: false),
                    CurrentVoyageId = table.Column<string>(type: "varchar(10)", nullable: true),
                    PreviousVoyageId = table.Column<string>(type: "varchar(10)", nullable: true),
                    TargetVoyageId = table.Column<string>(type: "varchar(10)", nullable: true),
                    RequestedAlternativeVoyageId = table.Column<string>(type: "varchar(10)", nullable: true),
                    RetryCount = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(2000)", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    LeaseOwner = table.Column<string>(type: "varchar(128)", nullable: true),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReplanningProcesses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Replanning_Bookings",
                        column: x => x.BookingId,
                        principalSchema: "dbo",
                        principalTable: "Bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VoyageAssignments",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VoyageId = table.Column<string>(type: "varchar(10)", nullable: false),
                    State = table.Column<string>(type: "varchar(32)", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    EffectiveAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    BookingId = table.Column<string>(type: "varchar(10)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VoyageAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VoyageAssignments_Bookings",
                        column: x => x.BookingId,
                        principalSchema: "dbo",
                        principalTable: "Bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CapacityReservations",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BookingId = table.Column<string>(type: "varchar(10)", nullable: false),
                    VoyageId = table.Column<string>(type: "varchar(10)", nullable: false),
                    ProcessId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Units = table.Column<int>(type: "int", nullable: false),
                    State = table.Column<string>(type: "varchar(32)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CapacityReservations", x => x.Id);
                    table.CheckConstraint("CK_CapacityReservations_Units", "[Units] > 0");
                    table.ForeignKey(
                        name: "FK_CapacityReservations_Bookings",
                        column: x => x.BookingId,
                        principalSchema: "dbo",
                        principalTable: "Bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CapacityReservations_Voyages",
                        column: x => x.VoyageId,
                        principalSchema: "dbo",
                        principalTable: "Voyages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CapacityReservations_BookingId",
                schema: "dbo",
                table: "CapacityReservations",
                column: "BookingId");

            migrationBuilder.CreateIndex(
                name: "IX_CapacityReservations_Voyage_State",
                schema: "dbo",
                table: "CapacityReservations",
                columns: new[] { "VoyageId", "State" });

            migrationBuilder.CreateIndex(
                name: "UX_CapacityReservations_Process_Voyage",
                schema: "dbo",
                table: "CapacityReservations",
                columns: new[] { "ProcessId", "VoyageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_Idempotency_Scope_Key",
                schema: "dbo",
                table: "IdempotencyRecords",
                columns: new[] { "Scope", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Outbox_Due",
                schema: "dbo",
                table: "OutboxMessages",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Outbox_Lease",
                schema: "dbo",
                table: "OutboxMessages",
                columns: new[] { "Status", "LeaseUntil" });

            migrationBuilder.CreateIndex(
                name: "IX_Replanning_Due",
                schema: "dbo",
                table: "ReplanningProcesses",
                columns: new[] { "State", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ReplanningProcesses_BookingId",
                schema: "dbo",
                table: "ReplanningProcesses",
                column: "BookingId");

            migrationBuilder.CreateIndex(
                name: "UX_Replanning_Disruption_Booking",
                schema: "dbo",
                table: "ReplanningProcesses",
                columns: new[] { "DisruptionId", "BookingId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VoyageAssignments_Voyage_State",
                schema: "dbo",
                table: "VoyageAssignments",
                columns: new[] { "VoyageId", "State" });

            migrationBuilder.CreateIndex(
                name: "UX_VoyageAssignments_Active_Booking",
                schema: "dbo",
                table: "VoyageAssignments",
                column: "BookingId",
                unique: true,
                filter: "[State] = 'Active'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CapacityReservations",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "IdempotencyRecords",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "OutboxMessages",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "ProcessedMessages",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "ReplanningProcesses",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "VoyageAssignments",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "Voyages",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "Bookings",
                schema: "dbo");
        }
    }
}
