using System.Text;
using Microsoft.EntityFrameworkCore;

namespace PMS.DataAccess.Persistence;

/// <summary>
/// Tables for reservations, gate sessions and payments (SRS section 8). Each statement only runs when
/// the table, column or index is missing, so it is safe on databases created from the full script.
/// </summary>
public static partial class AutomatedMigration
{
    private const string ActiveReservationStatuses = "'PendingDeposit','Confirmed','CheckedIn'";

    private const string OpenSessionStatuses = "'PendingEntry','Active','PendingPayment','Paid','LostTicketPending'";

    /// <summary>Columns added to tables that already existed: (table, column, SQL type).</summary>
    private static readonly (string Table, string Column, string Type)[] AddedColumns =
    [
        ("Reservations", "UserId", "[nvarchar](450) NULL"),
        ("Reservations", "VehicleTypeId", "[int] NULL"),
        ("Reservations", "DepositDueAt", "[datetime2](3) NULL"),
        ("Reservations", "ConfirmedAt", "[datetime2](3) NULL"),
        ("Reservations", "CheckedInAt", "[datetime2](3) NULL"),
        ("Reservations", "CompletedAt", "[datetime2](3) NULL"),
        ("Reservations", "CancelReason", "[nvarchar](500) NULL"),
        ("Reservations", "CancelledBy", "[varchar](20) NULL"),
        ("Reservations", "RefundAmount", "[decimal](18,2) NOT NULL DEFAULT 0"),
        ("Reservations", "ChangeCount", "[int] NOT NULL DEFAULT 0"),
        ("Reservations", "ReminderSentAt", "[datetime2](3) NULL"),
        ("Reservations", "HoldReminderSentAt", "[datetime2](3) NULL"),
        ("Reservations", "UpdatedAt", "[datetime2](3) NULL"),
        ("Reservations", "BookingMode", "[varchar](20) NULL"),
        ("Reservations", "DepartureTime", "[datetime2](3) NULL"),
        ("Reservations", "SlotHeldAt", "[datetime2](3) NULL"),

        ("ParkingSessions", "SessionType", "[varchar](20) NULL"),
        ("ParkingSessions", "VehicleTypeId", "[int] NULL"),
        ("ParkingSessions", "HasPlate", "[bit] NOT NULL DEFAULT 1"),
        ("ParkingSessions", "IsManualEntry", "[bit] NOT NULL DEFAULT 0"),
        ("ParkingSessions", "ManualEntryReason", "[nvarchar](500) NULL"),
        ("ParkingSessions", "IsManualExit", "[bit] NOT NULL DEFAULT 0"),
        ("ParkingSessions", "ManualExitReason", "[nvarchar](500) NULL"),
        ("ParkingSessions", "EntryConfirmedAt", "[datetime2](3) NULL"),
        ("ParkingSessions", "PricingRuleVersion", "[int] NULL"),
        ("ParkingSessions", "DepositApplied", "[decimal](18,2) NOT NULL DEFAULT 0"),
        ("ParkingSessions", "PaidAmount", "[decimal](18,2) NOT NULL DEFAULT 0"),
        ("ParkingSessions", "ExitWindowEndsAt", "[datetime2](3) NULL"),
        ("ParkingSessions", "ZeroFeeReason", "[nvarchar](100) NULL"),
        ("ParkingSessions", "PlateMismatch", "[bit] NOT NULL DEFAULT 0"),
        ("ParkingSessions", "OverrideNote", "[nvarchar](1000) NULL"),
        ("ParkingSessions", "ClosedNote", "[nvarchar](500) NULL"),
        ("ParkingSessions", "OverdueNotifiedAt", "[datetime2](3) NULL"),
        ("ParkingSessions", "AbandonedFlaggedAt", "[datetime2](3) NULL"),
        ("ParkingSessions", "CreatedAt", "[datetime2](3) NULL"),
        ("ParkingSessions", "UpdatedAt", "[datetime2](3) NULL"),

        // Lost-ticket cases: decision and approval mode (FR-OPS-05, BR-13, BR-14)
        ("SessionViolations", "Status", "[varchar](20) NULL"),
        ("SessionViolations", "ApprovalMode", "[varchar](20) NULL"),
        ("SessionViolations", "DecidedBy", "[nvarchar](450) NULL"),
        ("SessionViolations", "DecidedAt", "[datetime2](3) NULL"),
        ("SessionViolations", "DecisionNote", "[nvarchar](500) NULL"),

        ("Invoices", "ReservationId", "[bigint] NULL"),

        ("Payments", "Purpose", "[varchar](20) NULL"),
        ("Payments", "ReservationId", "[bigint] NULL"),
        ("Payments", "SessionId", "[bigint] NULL"),
        ("Payments", "ParkingLotId", "[int] NULL"),
        ("Payments", "CustomerUserId", "[nvarchar](450) NULL"),
        ("Payments", "CollectedByUserId", "[nvarchar](450) NULL"),
        ("Payments", "Channel", "[varchar](10) NULL"),
        ("Payments", "ExpiresAt", "[datetime2](3) NULL"),
        ("Payments", "RefundOfPaymentId", "[bigint] NULL"),
        ("Payments", "RefundedAmount", "[decimal](18,2) NOT NULL DEFAULT 0"),
        ("Payments", "GatewayTransactionNo", "[nvarchar](100) NULL"),
        ("Payments", "FailureReason", "[nvarchar](500) NULL"),
        ("Payments", "PayUrl", "[nvarchar](2000) NULL"),
        ("Payments", "LastCheckedAt", "[datetime2](3) NULL"),
        ("Payments", "UpdatedAt", "[datetime2](3) NULL")
    ];

    private static async Task EnsureOperationsTablesAsync(PmsDbContext pmsContext)
    {
        const string tables = @"
            IF OBJECT_ID('dbo.Gates', 'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[Gates](
                    [Id] [int] IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [ParkingLotId] [int] NOT NULL,
                    [Code] [nvarchar](30) NOT NULL,
                    [Name] [nvarchar](200) NOT NULL,
                    [Direction] [varchar](10) NOT NULL DEFAULT 'Both',
                    [ZoneId] [int] NULL,
                    [IsActive] [bit] NOT NULL DEFAULT 1,
                    CONSTRAINT [FK_Gates_ParkingLots] FOREIGN KEY([ParkingLotId]) REFERENCES [dbo].[ParkingLots] ([Id])
                );
                CREATE NONCLUSTERED INDEX [IX_Gates_ParkingLotId] ON [dbo].[Gates]([ParkingLotId] ASC);
            END

            IF OBJECT_ID('dbo.PricingRules', 'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[PricingRules](
                    [Id] [int] IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [Code] [nvarchar](40) NOT NULL,
                    [Name] [nvarchar](200) NOT NULL,
                    [ParkingLotId] [int] NULL,
                    [ZoneId] [int] NULL,
                    [VehicleTypeId] [int] NOT NULL,
                    [ChargeModel] [varchar](20) NOT NULL,
                    [EntryFee] [decimal](18,2) NOT NULL DEFAULT 0,
                    [FirstBlockMinutes] [int] NULL,
                    [FirstBlockPrice] [decimal](18,2) NULL,
                    [BlockMinutes] [int] NULL,
                    [BlockPrice] [decimal](18,2) NULL,
                    [DayStartTime] [time](0) NULL,
                    [NightStartTime] [time](0) NULL,
                    [DayPrice] [decimal](18,2) NULL,
                    [NightPrice] [decimal](18,2) NULL,
                    [MaxDailyFee] [decimal](18,2) NULL,
                    [GraceMinutes] [int] NOT NULL DEFAULT 0,
                    [OvernightSurcharge] [decimal](18,2) NOT NULL DEFAULT 0,
                    [OverstayAfterHours] [int] NULL,
                    [OverstaySurchargePerDay] [decimal](18,2) NOT NULL DEFAULT 0,
                    [LostTicketFee] [decimal](18,2) NOT NULL DEFAULT 0,
                    [NoShowFee] [decimal](18,2) NOT NULL DEFAULT 0,
                    [Currency] [char](3) NOT NULL DEFAULT 'VND',
                    [Priority] [int] NOT NULL DEFAULT 0,
                    [EffectiveFrom] [datetime2](0) NOT NULL,
                    [EffectiveTo] [datetime2](0) NULL,
                    [Status] [varchar](20) NOT NULL DEFAULT 'Draft',
                    [VersionNo] [int] NOT NULL DEFAULT 1,
                    [ApprovedBy] [nvarchar](450) NULL,
                    [ApprovedAt] [datetime2](0) NULL,
                    [CreatedBy] [nvarchar](450) NULL,
                    [CreatedAt] [datetime2](3) NOT NULL DEFAULT (sysutcdatetime()),
                    [UpdatedAt] [datetime2](3) NULL
                );
                CREATE NONCLUSTERED INDEX [IX_PricingRules_Lot_VehicleType] ON [dbo].[PricingRules]([ParkingLotId] ASC, [VehicleTypeId] ASC, [Status] ASC);
            END

            IF OBJECT_ID('dbo.PricingRuleHistories', 'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[PricingRuleHistories](
                    [Id] [bigint] IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [PricingRuleId] [int] NOT NULL,
                    [ChangeType] [varchar](20) NOT NULL,
                    [OldValuesJson] [nvarchar](max) NULL,
                    [NewValuesJson] [nvarchar](max) NULL,
                    [ChangedBy] [nvarchar](450) NULL,
                    [ChangedAt] [datetime2](3) NOT NULL DEFAULT (sysutcdatetime()),
                    [Reason] [nvarchar](500) NULL,
                    CONSTRAINT [FK_PricingRuleHistories_PricingRules] FOREIGN KEY([PricingRuleId]) REFERENCES [dbo].[PricingRules] ([Id])
                );
                CREATE NONCLUSTERED INDEX [IX_PricingRuleHistories_PricingRuleId] ON [dbo].[PricingRuleHistories]([PricingRuleId] ASC);
            END

            IF OBJECT_ID('dbo.Reservations', 'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[Reservations](
                    [Id] [bigint] IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [ReservationCode] [nvarchar](40) NOT NULL,
                    [CustomerId] [int] NOT NULL,
                    [VehicleId] [uniqueidentifier] NOT NULL,
                    [ParkingLotId] [int] NOT NULL,
                    [ZoneId] [int] NULL,
                    [SlotId] [int] NULL,
                    [StartTime] [datetime2](0) NOT NULL,
                    [EndTime] [datetime2](0) NOT NULL,
                    [DepositAmount] [decimal](18,2) NOT NULL DEFAULT 0,
                    [Status] [varchar](20) NOT NULL DEFAULT 'Pending',
                    [CancelledAt] [datetime2](3) NULL,
                    [CreatedAt] [datetime2](3) NOT NULL DEFAULT (sysutcdatetime()),
                    CONSTRAINT [FK_Reservations_Customers] FOREIGN KEY([CustomerId]) REFERENCES [dbo].[Customers] ([Id]),
                    CONSTRAINT [FK_Reservations_Vehicles] FOREIGN KEY([VehicleId]) REFERENCES [dbo].[Vehicles] ([Id]),
                    CONSTRAINT [FK_Reservations_ParkingLots] FOREIGN KEY([ParkingLotId]) REFERENCES [dbo].[ParkingLots] ([Id]),
                    CONSTRAINT [FK_Reservations_Slots] FOREIGN KEY([SlotId]) REFERENCES [dbo].[Slots] ([Id])
                );
                CREATE UNIQUE NONCLUSTERED INDEX [IX_Reservations_ReservationCode] ON [dbo].[Reservations]([ReservationCode] ASC);
            END

            IF OBJECT_ID('dbo.ParkingSessions', 'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[ParkingSessions](
                    [Id] [bigint] IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [SessionCode] [nvarchar](40) NOT NULL,
                    [ParkingLotId] [int] NOT NULL,
                    [CustomerId] [int] NULL,
                    [VehicleId] [uniqueidentifier] NULL,
                    [AccessCardId] [int] NULL,
                    [SubscriptionId] [bigint] NULL,
                    [ReservationId] [bigint] NULL,
                    [EntryGateId] [int] NOT NULL,
                    [EntryTime] [datetime2](0) NOT NULL,
                    [EntryPlateNumber] [nvarchar](20) NOT NULL,
                    [EntryPlateNormalized] [nvarchar](20) NOT NULL,
                    [EntryImageFrontUrl] [nvarchar](500) NULL,
                    [EntryImageRearUrl] [nvarchar](500) NULL,
                    [EntryImageOverviewUrl] [nvarchar](500) NULL,
                    [EntryOperatorUserId] [nvarchar](450) NULL,
                    [EntryAnprConfidence] [decimal](5,2) NULL,
                    [ZoneId] [int] NULL,
                    [SlotId] [int] NULL,
                    [ExitGateId] [int] NULL,
                    [ExitTime] [datetime2](0) NULL,
                    [ExitPlateNumber] [nvarchar](20) NULL,
                    [ExitPlateNormalized] [nvarchar](20) NULL,
                    [ExitImageFrontUrl] [nvarchar](500) NULL,
                    [ExitImageRearUrl] [nvarchar](500) NULL,
                    [ExitImageOverviewUrl] [nvarchar](500) NULL,
                    [ExitOperatorUserId] [nvarchar](450) NULL,
                    [ExitAnprConfidence] [decimal](5,2) NULL,
                    [PricingRuleId] [int] NULL,
                    [TotalDurationMinutes] [int] NULL,
                    [OriginalFee] [decimal](18,2) NOT NULL DEFAULT 0,
                    [DiscountAmount] [decimal](18,2) NOT NULL DEFAULT 0,
                    [FinalFee] [decimal](18,2) NOT NULL DEFAULT 0,
                    [Status] [varchar](20) NOT NULL DEFAULT 'Ongoing',
                    [IsLostCard] [bit] NOT NULL DEFAULT 0,
                    [RowVersion] [rowversion] NOT NULL,
                    CONSTRAINT [FK_ParkingSessions_ParkingLots] FOREIGN KEY([ParkingLotId]) REFERENCES [dbo].[ParkingLots] ([Id]),
                    CONSTRAINT [FK_ParkingSessions_Customers] FOREIGN KEY([CustomerId]) REFERENCES [dbo].[Customers] ([Id]),
                    CONSTRAINT [FK_ParkingSessions_Vehicles] FOREIGN KEY([VehicleId]) REFERENCES [dbo].[Vehicles] ([Id]),
                    CONSTRAINT [FK_ParkingSessions_EntryGates] FOREIGN KEY([EntryGateId]) REFERENCES [dbo].[Gates] ([Id]),
                    CONSTRAINT [FK_ParkingSessions_ExitGates] FOREIGN KEY([ExitGateId]) REFERENCES [dbo].[Gates] ([Id])
                );
                CREATE NONCLUSTERED INDEX [IX_ParkingSessions_EntryPlateNormalized_Status] ON [dbo].[ParkingSessions]([EntryPlateNormalized] ASC, [Status] ASC);
                CREATE NONCLUSTERED INDEX [IX_ParkingSessions_ParkingLotId_Status] ON [dbo].[ParkingSessions]([ParkingLotId] ASC, [Status] ASC);
                CREATE UNIQUE NONCLUSTERED INDEX [IX_ParkingSessions_SessionCode] ON [dbo].[ParkingSessions]([SessionCode] ASC);
            END

            IF OBJECT_ID('dbo.Invoices', 'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[Invoices](
                    [Id] [bigint] IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [InvoiceNumber] [nvarchar](50) NOT NULL,
                    [SessionId] [bigint] NULL,
                    [SubscriptionId] [bigint] NULL,
                    [CustomerId] [int] NULL,
                    [ParkingLotId] [int] NOT NULL,
                    [SubTotal] [decimal](18,2) NOT NULL DEFAULT 0,
                    [DiscountAmount] [decimal](18,2) NOT NULL DEFAULT 0,
                    [TaxAmount] [decimal](18,2) NOT NULL DEFAULT 0,
                    [TotalAmount] [decimal](18,2) NOT NULL DEFAULT 0,
                    [Status] [varchar](20) NOT NULL DEFAULT 'Unpaid',
                    [DueDate] [datetime2](0) NULL,
                    [PaidAt] [datetime2](3) NULL,
                    [CreatedAt] [datetime2](3) NOT NULL DEFAULT (sysutcdatetime()),
                    CONSTRAINT [FK_Invoices_ParkingSessions] FOREIGN KEY([SessionId]) REFERENCES [dbo].[ParkingSessions] ([Id])
                );
                CREATE UNIQUE NONCLUSTERED INDEX [IX_Invoices_InvoiceNumber] ON [dbo].[Invoices]([InvoiceNumber] ASC);
            END

            IF OBJECT_ID('dbo.Payments', 'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[Payments](
                    [Id] [bigint] IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [PaymentCode] [nvarchar](50) NOT NULL,
                    [InvoiceId] [bigint] NOT NULL,
                    [PaymentMethod] [varchar](20) NOT NULL,
                    [GatewayProvider] [varchar](30) NULL,
                    [Amount] [decimal](18,2) NOT NULL,
                    [Currency] [char](3) NOT NULL DEFAULT 'VND',
                    [Status] [varchar](20) NOT NULL DEFAULT 'Pending',
                    [TransactionRef] [nvarchar](100) NULL,
                    [PayloadResponse] [nvarchar](max) NULL,
                    [PaidAt] [datetime2](3) NULL,
                    [CreatedAt] [datetime2](3) NOT NULL DEFAULT (sysutcdatetime()),
                    CONSTRAINT [FK_Payments_Invoices] FOREIGN KEY([InvoiceId]) REFERENCES [dbo].[Invoices] ([Id])
                );
                CREATE UNIQUE NONCLUSTERED INDEX [IX_Payments_PaymentCode] ON [dbo].[Payments]([PaymentCode] ASC);
            END

            IF OBJECT_ID('dbo.SessionViolations', 'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[SessionViolations](
                    [Id] [bigint] IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [SessionId] [bigint] NOT NULL,
                    [ViolationType] [varchar](30) NOT NULL,
                    [FineAmount] [decimal](18,2) NOT NULL DEFAULT 0,
                    [Note] [nvarchar](500) NULL,
                    [ReportedBy] [nvarchar](450) NULL,
                    [ReportedAt] [datetime2](3) NOT NULL DEFAULT (sysutcdatetime()),
                    [IsResolved] [bit] NOT NULL DEFAULT 0
                );
                CREATE NONCLUSTERED INDEX [IX_SessionViolations_SessionId] ON [dbo].[SessionViolations]([SessionId] ASC);
            END

            -- Whitelist / Blacklist (BR-16, BR-17)
            IF OBJECT_ID('dbo.PlateLists', 'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[PlateLists](
                    [Id] [int] IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [ListType] [varchar](10) NOT NULL,
                    [PlateNumber] [nvarchar](20) NOT NULL,
                    [PlateNormalized] [nvarchar](20) NOT NULL,
                    [ParkingLotId] [int] NULL,
                    [Reason] [nvarchar](500) NOT NULL,
                    [ExpiresAt] [datetime2](0) NOT NULL,
                    [IsActive] [bit] NOT NULL DEFAULT 1,
                    [CreatedBy] [nvarchar](450) NULL,
                    [CreatedAt] [datetime2](3) NOT NULL DEFAULT (sysutcdatetime())
                );
                CREATE NONCLUSTERED INDEX [IX_PlateLists_PlateNormalized_ListType] ON [dbo].[PlateLists]([PlateNormalized] ASC, [ListType] ASC);
            END

            IF OBJECT_ID('dbo.ReservationChangeLogs', 'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[ReservationChangeLogs](
                    [Id] [bigint] IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [ReservationId] [bigint] NOT NULL,
                    [Action] [varchar](30) NOT NULL,
                    [OldValue] [nvarchar](200) NULL,
                    [NewValue] [nvarchar](200) NULL,
                    [ChangedBy] [nvarchar](450) NULL,
                    [ChangedAt] [datetime2](3) NOT NULL DEFAULT (sysutcdatetime())
                );
                CREATE NONCLUSTERED INDEX [IX_ReservationChangeLogs_ReservationId] ON [dbo].[ReservationChangeLogs]([ReservationId] ASC);
            END
        ";

        await pmsContext.Database.ExecuteSqlRawAsync(tables);

        var columns = new StringBuilder();
        foreach (var (table, column, type) in AddedColumns)
            columns.AppendLine($"IF COL_LENGTH('dbo.{table}', '{column}') IS NULL ALTER TABLE [dbo].[{table}] ADD [{column}] {type};");
        await pmsContext.Database.ExecuteSqlRawAsync(columns.ToString());

        // Bookings made before date-time booking: pay-as-you-go, and the active ones already hold their bay.
        // A bay can now carry several bookings at different times, so the old "one active booking per bay"
        // index gives way to "one booking holding the bay" (IX_Reservations_SlotId_Held below).
        await pmsContext.Database.ExecuteSqlRawAsync($@"
            UPDATE [dbo].[Reservations] SET [BookingMode] = 'PayAsYouGo' WHERE [BookingMode] IS NULL;
            UPDATE [dbo].[Reservations] SET [SlotHeldAt] = [CreatedAt]
                WHERE [SlotHeldAt] IS NULL AND [SlotId] IS NOT NULL AND [Status] IN ({ActiveReservationStatuses});
            IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Reservations_SlotId_Active')
                DROP INDEX [IX_Reservations_SlotId_Active] ON [dbo].[Reservations];");

        // Separate batches: the filtered columns may have been added just above. Old rows that break a
        // unique index (e.g. duplicate open sessions from an earlier import) must not stop the API starting,
        // so each index is created in its own TRY block; the application checks the same rules anyway.
        // NFR-15: never two bookings holding one slot. NFR-14: one open session per plate and lot.
        string[] indexes =
        [
            $@"CREATE UNIQUE NONCLUSTERED INDEX [IX_Reservations_SlotId_Held] ON [dbo].[Reservations]([SlotId] ASC)
               WHERE [SlotId] IS NOT NULL AND [SlotHeldAt] IS NOT NULL AND [Status] IN ({ActiveReservationStatuses})",
            "CREATE NONCLUSTERED INDEX [IX_Reservations_UserId_Status] ON [dbo].[Reservations]([UserId] ASC, [Status] ASC)",
            $@"CREATE UNIQUE NONCLUSTERED INDEX [IX_ParkingSessions_OpenPlate] ON [dbo].[ParkingSessions]([ParkingLotId] ASC, [EntryPlateNormalized] ASC)
               WHERE [HasPlate] = 1 AND [Status] IN ({OpenSessionStatuses})",
            "CREATE NONCLUSTERED INDEX [IX_Payments_Status_CreatedAt] ON [dbo].[Payments]([Status] ASC, [CreatedAt] ASC)"
        ];
        foreach (var create in indexes)
        {
            var name = create[(create.IndexOf('[') + 1)..create.IndexOf(']')];
            await pmsContext.Database.ExecuteSqlRawAsync(
                $"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = '{name}') BEGIN TRY EXEC('{create.Replace("'", "''")}') END TRY BEGIN CATCH END CATCH");
        }
    }
}
