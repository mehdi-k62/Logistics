CREATE TABLE dbo.Voyages
(
    Id VARCHAR(10) NOT NULL CONSTRAINT PK_Voyages PRIMARY KEY,
    State VARCHAR(32) NOT NULL,
    TotalCapacity INT NOT NULL,
    ReservedCapacity INT NOT NULL CONSTRAINT DF_Voyages_Reserved DEFAULT 0,
    Version BIGINT NOT NULL CONSTRAINT DF_Voyages_Version DEFAULT 0,
    CONSTRAINT CK_Voyages_Capacity CHECK (TotalCapacity > 0 AND ReservedCapacity >= 0 AND ReservedCapacity <= TotalCapacity)
);

CREATE TABLE dbo.Bookings
(
    Id VARCHAR(10) NOT NULL CONSTRAINT PK_Bookings PRIMARY KEY,
    RequestedTeu INT NOT NULL,
    State VARCHAR(32) NOT NULL,
    Version BIGINT NOT NULL CONSTRAINT DF_Bookings_Version DEFAULT 0,
    CONSTRAINT CK_Bookings_RequestedTeu CHECK (RequestedTeu > 0)
);

CREATE TABLE dbo.VoyageAssignments
(
    Id UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_VoyageAssignments PRIMARY KEY,
    BookingId VARCHAR(10) NOT NULL,
    VoyageId VARCHAR(10) NOT NULL,
    State VARCHAR(32) NOT NULL,
    Sequence BIGINT NOT NULL,
    EffectiveAt DATETIMEOFFSET NOT NULL,
    CONSTRAINT FK_VoyageAssignments_Bookings FOREIGN KEY (BookingId) REFERENCES dbo.Bookings(Id)
);

CREATE UNIQUE INDEX UX_VoyageAssignments_Active_Booking
ON dbo.VoyageAssignments(BookingId)
WHERE State = 'Active';

CREATE INDEX IX_VoyageAssignments_Voyage_State
ON dbo.VoyageAssignments(VoyageId, State);

CREATE TABLE dbo.ReplanningProcesses
(
    Id UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_ReplanningProcesses PRIMARY KEY,
    BookingId VARCHAR(10) NOT NULL,
    DisruptionId UNIQUEIDENTIFIER NOT NULL,
    State VARCHAR(64) NOT NULL,
    CurrentVoyageId VARCHAR(10) NULL,
    PreviousVoyageId VARCHAR(10) NULL,
    TargetVoyageId VARCHAR(10) NULL,
    RetryCount INT NOT NULL DEFAULT 0,
    NextAttemptAt DATETIMEOFFSET NULL,
    LastError NVARCHAR(2000) NULL,
    Version BIGINT NOT NULL DEFAULT 0,
    CreatedAt DATETIMEOFFSET NOT NULL,
    UpdatedAt DATETIMEOFFSET NOT NULL,
    CompletedAt DATETIMEOFFSET NULL,
    LeaseOwner VARCHAR(128) NULL,
    LeaseUntil DATETIMEOFFSET NULL,
    CONSTRAINT FK_Replanning_Bookings FOREIGN KEY (BookingId) REFERENCES dbo.Bookings(Id)
);

CREATE UNIQUE INDEX UX_Replanning_Disruption_Booking
ON dbo.ReplanningProcesses(DisruptionId, BookingId);

CREATE INDEX IX_Replanning_Due
ON dbo.ReplanningProcesses(State, NextAttemptAt);

CREATE TABLE dbo.CapacityReservations
(
    Id UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_CapacityReservations PRIMARY KEY,
    BookingId VARCHAR(10) NOT NULL,
    VoyageId VARCHAR(10) NOT NULL,
    ProcessId UNIQUEIDENTIFIER NOT NULL,
    Units INT NOT NULL,
    State VARCHAR(32) NOT NULL,
    CreatedAt DATETIMEOFFSET NOT NULL,
    ReleasedAt DATETIMEOFFSET NULL,
    CONSTRAINT CK_CapacityReservations_Units CHECK (Units > 0),
    CONSTRAINT FK_CapacityReservations_Bookings FOREIGN KEY (BookingId) REFERENCES dbo.Bookings(Id),
    CONSTRAINT FK_CapacityReservations_Voyages FOREIGN KEY (VoyageId) REFERENCES dbo.Voyages(Id)
);

CREATE UNIQUE INDEX UX_CapacityReservations_Process_Voyage
ON dbo.CapacityReservations(ProcessId, VoyageId);

CREATE INDEX IX_CapacityReservations_Voyage_State
ON dbo.CapacityReservations(VoyageId, State);

CREATE TABLE dbo.OutboxMessages
(
    Id UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_OutboxMessages PRIMARY KEY,
    MessageType VARCHAR(256) NOT NULL,
    AggregateId UNIQUEIDENTIFIER NOT NULL,
    Payload NVARCHAR(MAX) NOT NULL,
    OccurredAt DATETIMEOFFSET NOT NULL,
    Status VARCHAR(32) NOT NULL,
    Attempts INT NOT NULL DEFAULT 0,
    NextAttemptAt DATETIMEOFFSET NOT NULL,
    PublishedAt DATETIMEOFFSET NULL,
    LastError NVARCHAR(2000) NULL,
    LeaseOwner VARCHAR(128) NULL,
    LeaseUntil DATETIMEOFFSET NULL
);

CREATE INDEX IX_Outbox_Due ON dbo.OutboxMessages(Status, NextAttemptAt);
CREATE INDEX IX_Outbox_Lease ON dbo.OutboxMessages(Status, LeaseUntil);

CREATE TABLE dbo.ProcessedMessages
(
    ConsumerName VARCHAR(256) NOT NULL,
    MessageId UNIQUEIDENTIFIER NOT NULL,
    ProcessedAt DATETIMEOFFSET NOT NULL,
    CONSTRAINT PK_ProcessedMessages PRIMARY KEY (ConsumerName, MessageId)
);

CREATE TABLE dbo.IdempotencyRecords
(
    Id UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_IdempotencyRecords PRIMARY KEY,
    Scope VARCHAR(512) NOT NULL,
    [Key] VARCHAR(256) NOT NULL,
    PayloadHash VARCHAR(128) NOT NULL,
    ProcessId UNIQUEIDENTIFIER NOT NULL,
    Status VARCHAR(32) NOT NULL,
    ResponsePayload NVARCHAR(MAX) NULL,
    CreatedAt DATETIMEOFFSET NOT NULL,
    ExpiresAt DATETIMEOFFSET NOT NULL
);

CREATE UNIQUE INDEX UX_Idempotency_Scope_Key ON dbo.IdempotencyRecords(Scope, [Key]);
GO

CREATE OR ALTER PROCEDURE dbo.ReserveCapacity
    @VoyageId VARCHAR(10),
    @BookingId VARCHAR(10),
    @ProcessId UNIQUEIDENTIFIER,
    @Units INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;

    UPDATE dbo.Voyages
    SET ReservedCapacity = ReservedCapacity + @Units, Version = Version + 1
    WHERE Id = @VoyageId
      AND State = 'Available'
      AND ReservedCapacity + @Units <= TotalCapacity;

    IF @@ROWCOUNT = 0
    BEGIN
        ROLLBACK;
        THROW 51001, 'Capacity unavailable or voyage is not available.', 1;
    END;

    INSERT INTO dbo.CapacityReservations
        (Id, BookingId, VoyageId, ProcessId, Units, State, CreatedAt)
    VALUES
        (NEWID(), @BookingId, @VoyageId, @ProcessId, @Units, 'Active', SYSUTCDATETIME());

    COMMIT;
END;
GO
