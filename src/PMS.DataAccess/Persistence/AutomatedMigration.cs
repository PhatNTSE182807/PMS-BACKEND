using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PMS.DataAccess.Identity;

namespace PMS.DataAccess.Persistence;

public static partial class AutomatedMigration
{
    public static async Task MigrateAsync(IServiceProvider services)
    {
        var logger = services.GetService<ILogger<DatabaseContext>>();

        try
        {
            var context = services.GetRequiredService<DatabaseContext>();

            if (context.Database.IsSqlServer())
            {
                logger?.LogInformation("Applying database migrations...");
                await context.Database.MigrateAsync();
                logger?.LogInformation("Database migration completed successfully.");
            }

            var pmsContext = services.GetRequiredService<PmsDbContext>();
            if (pmsContext.Database.IsSqlServer())
            {
                await EnsurePmsTablesAsync(pmsContext);
                // Reservations, gate sessions and payments (AutomatedMigration.Operations.cs)
                await EnsureOperationsTablesAsync(pmsContext);
                await EnsureWalletTablesAsync(pmsContext);
                await RemoveRejectedVehiclesAsync(pmsContext);
                await EnsureGateRequestsTableAsync(pmsContext);
                await EnsureNotificationsTableAsync(pmsContext);
                await EnsureSystemSettingsTableAsync(pmsContext);
                await EnsureCustomerAvatarsTableAsync(pmsContext);
                await SetBookedBayCompensationDefaultsAsync(pmsContext);
            }

            var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();

            await DatabaseContextSeed.SeedDatabaseAsync(context, pmsContext, userManager);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "An error occurred while migrating or seeding the database. Please check your SQL Server connection string.");
            throw;
        }
    }

    private static async Task EnsurePmsTablesAsync(PmsDbContext pmsContext)
    {
        const string sql = @"
            IF OBJECT_ID('dbo.Users', 'U') IS NOT NULL AND COL_LENGTH('dbo.Users', 'IsActive') IS NULL
            BEGIN
                ALTER TABLE [dbo].[Users] ADD [IsActive] [bit] NOT NULL DEFAULT 1;
            END

            IF OBJECT_ID('dbo.VehicleTypes', 'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[VehicleTypes](
                    [Id] [int] IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [Code] [varchar](30) NOT NULL,
                    [Name] [nvarchar](100) NOT NULL,
                    [Description] [nvarchar](300) NULL,
                    [SortOrder] [int] NOT NULL DEFAULT 0,
                    [IsActive] [bit] NOT NULL DEFAULT 1
                );
            END

            IF NOT EXISTS (SELECT 1 FROM [dbo].[VehicleTypes])
            BEGIN
                INSERT INTO [dbo].[VehicleTypes] (Code, Name, Description, SortOrder, IsActive)
                VALUES 
                    ('CAR_4', '4-Seat Car', 'Sedan/Hatchback up to 5 seats', 1, 1),
                    ('CAR_7', '7-Seat Car', 'SUV/MPV up to 7 seats', 2, 1),
                    ('MOTORBIKE', 'Motorbike', 'Two-wheeled motorcycles', 3, 1);
            END

            -- Only 4-seat cars, 7-seat cars and motorbikes are accepted (team decision, 2026-10-25).
            -- Older databases keep their Bicycle row (vehicles and history may point to it) but it is turned off.
            UPDATE [dbo].[VehicleTypes] SET [IsActive] = 0 WHERE [Code] IN ('BICYCLE', 'EV') AND [IsActive] = 1;

            IF OBJECT_ID('dbo.Customers', 'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[Customers](
                    [Id] [int] IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [UserId] [nvarchar](450) NULL,
                    [CustomerCode] [nvarchar](30) NOT NULL,
                    [FullName] [nvarchar](200) NOT NULL,
                    [Phone] [nvarchar](30) NULL,
                    [Email] [nvarchar](256) NULL,
                    [AvatarUrl] [nvarchar](500) NULL,
                    [DateOfBirth] [date] NULL,
                    [Gender] [varchar](10) NULL,
                    [Address] [nvarchar](500) NULL,
                    [CustomerType] [varchar](20) NOT NULL DEFAULT 'Individual',
                    [CorporateAccountId] [int] NULL,
                    [LoyaltyPoints] [int] NOT NULL DEFAULT 0,
                    [MembershipTier] [varchar](20) NOT NULL DEFAULT 'Standard',
                    [IsActive] [bit] NOT NULL DEFAULT 1,
                    [AnonymizedAt] [datetime2](3) NULL,
                    [CreatedAt] [datetime2](3) NOT NULL DEFAULT (sysutcdatetime()),
                    [UpdatedAt] [datetime2](3) NULL
                );
                CREATE NONCLUSTERED INDEX [IX_Customers_UserId] ON [dbo].[Customers]([UserId] ASC);
            END
            ELSE
            BEGIN
                IF COL_LENGTH('dbo.Customers', 'AvatarUrl') IS NULL
                BEGIN
                    ALTER TABLE [dbo].[Customers] ADD [AvatarUrl] [nvarchar](500) NULL;
                END
            END

            IF OBJECT_ID('dbo.Vehicles', 'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[Vehicles](
                    [Id] [uniqueidentifier] NOT NULL PRIMARY KEY DEFAULT (NEWID()),
                    [CustomerId] [int] NULL,
                    [VehicleTypeId] [int] NOT NULL,
                    [PlateNumber] [nvarchar](20) NOT NULL,
                    [PlateNormalized] [nvarchar](20) NOT NULL,
                    [Brand] [nvarchar](100) NULL,
                    [Model] [nvarchar](100) NULL,
                    [Color] [nvarchar](50) NULL,
                    [IsDefault] [bit] NOT NULL DEFAULT 0,
                    [IsActive] [bit] NOT NULL DEFAULT 1,
                    [CreatedAt] [datetime2](3) NOT NULL DEFAULT (sysutcdatetime()),
                    [UpdatedAt] [datetime2](3) NULL,
                    CONSTRAINT [FK_Vehicles_Customers] FOREIGN KEY([CustomerId]) REFERENCES [dbo].[Customers] ([Id]),
                    CONSTRAINT [FK_Vehicles_VehicleTypes] FOREIGN KEY([VehicleTypeId]) REFERENCES [dbo].[VehicleTypes] ([Id])
                );
                CREATE NONCLUSTERED INDEX [IX_Vehicles_PlateNormalized] ON [dbo].[Vehicles]([PlateNormalized] ASC);
            END
            ELSE
            BEGIN
                IF EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'Vehicles' AND COLUMN_NAME = 'Id' AND DATA_TYPE = 'int')
                BEGIN
                    IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Vehicles_PlateNormalized')
                        DROP INDEX [IX_Vehicles_PlateNormalized] ON [dbo].[Vehicles];

                    CREATE TABLE [dbo].[Vehicles_Temp](
                        [Id] [uniqueidentifier] NOT NULL PRIMARY KEY DEFAULT (NEWID()),
                        [CustomerId] [int] NULL,
                        [VehicleTypeId] [int] NOT NULL,
                        [PlateNumber] [nvarchar](20) NOT NULL,
                        [PlateNormalized] [nvarchar](20) NOT NULL,
                        [Brand] [nvarchar](100) NULL,
                        [Model] [nvarchar](100) NULL,
                        [Color] [nvarchar](50) NULL,
                        [IsDefault] [bit] NOT NULL DEFAULT 0,
                        [IsActive] [bit] NOT NULL DEFAULT 1,
                        [CreatedAt] [datetime2](3) NOT NULL DEFAULT (sysutcdatetime()),
                        [UpdatedAt] [datetime2](3) NULL,
                        CONSTRAINT [FK_VehiclesTemp_Customers] FOREIGN KEY([CustomerId]) REFERENCES [dbo].[Customers] ([Id]),
                        CONSTRAINT [FK_VehiclesTemp_VehicleTypes] FOREIGN KEY([VehicleTypeId]) REFERENCES [dbo].[VehicleTypes] ([Id])
                    );

                    INSERT INTO [dbo].[Vehicles_Temp] ([Id], [CustomerId], [VehicleTypeId], [PlateNumber], [PlateNormalized], [Brand], [Model], [Color], [IsDefault], [IsActive], [CreatedAt], [UpdatedAt])
                    SELECT NEWID(), [CustomerId], [VehicleTypeId], [PlateNumber], [PlateNormalized], [Brand], [Model], [Color], [IsDefault], [IsActive], [CreatedAt], [UpdatedAt]
                    FROM [dbo].[Vehicles];

                    DROP TABLE [dbo].[Vehicles];

                    EXEC sp_rename 'dbo.Vehicles_Temp', 'Vehicles';

                    CREATE NONCLUSTERED INDEX [IX_Vehicles_PlateNormalized] ON [dbo].[Vehicles]([PlateNormalized] ASC);
                END

                IF COL_LENGTH('dbo.Vehicles', 'IsDefault') IS NULL
                BEGIN
                    ALTER TABLE [dbo].[Vehicles] ADD [IsDefault] [bit] NOT NULL DEFAULT 0;
                END
            END

            -- Citizen ID (CCCD, 12 digits): the customer's own, and the registered owner of each vehicle
            IF COL_LENGTH('dbo.Customers', 'CitizenId') IS NULL
            BEGIN
                ALTER TABLE [dbo].[Customers] ADD [CitizenId] [varchar](12) NULL;
            END

            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Customers_CitizenId')
            BEGIN
                -- Dynamic SQL: the column may have been added earlier in this same batch
                EXEC('CREATE UNIQUE NONCLUSTERED INDEX [IX_Customers_CitizenId] ON [dbo].[Customers]([CitizenId] ASC) WHERE [CitizenId] IS NOT NULL');
            END

            IF COL_LENGTH('dbo.Vehicles', 'CitizenId') IS NULL
            BEGIN
                ALTER TABLE [dbo].[Vehicles] ADD [CitizenId] [varchar](12) NULL;
            END

            IF OBJECT_ID('dbo.Employees', 'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[Employees](
                    [Id] [int] IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [UserId] [nvarchar](450) NOT NULL,
                    [EmployeeCode] [nvarchar](30) NOT NULL,
                    [FullName] [nvarchar](200) NOT NULL,
                    [Phone] [nvarchar](30) NULL,
                    [Email] [nvarchar](256) NULL,
                    [Position] [varchar](30) NOT NULL DEFAULT 'Attendant',
                    [ParkingLotId] [int] NULL,
                    [HireDate] [date] NULL,
                    [TerminationDate] [date] NULL,
                    [Status] [varchar](20) NOT NULL DEFAULT 'Active',
                    [CreatedAt] [datetime2](3) NOT NULL DEFAULT (sysutcdatetime()),
                    [UpdatedAt] [datetime2](3) NULL
                );
                CREATE UNIQUE NONCLUSTERED INDEX [IX_Employees_EmployeeCode] ON [dbo].[Employees]([EmployeeCode] ASC);
                CREATE NONCLUSTERED INDEX [IX_Employees_UserId] ON [dbo].[Employees]([UserId] ASC);
            END

            IF OBJECT_ID('dbo.ParkingLots', 'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[ParkingLots](
                    [Id] [int] IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [Code] [nvarchar](30) NOT NULL,
                    [Name] [nvarchar](200) NOT NULL,
                    [Address] [nvarchar](500) NOT NULL,
                    [City] [nvarchar](100) NULL,
                    [District] [nvarchar](100) NULL,
                    [Latitude] [decimal](9,6) NULL,
                    [Longitude] [decimal](9,6) NULL,
                    [OpenTime] [time](0) NULL,
                    [CloseTime] [time](0) NULL,
                    [Is24Hours] [bit] NOT NULL DEFAULT 1,
                    [Capacity] [int] NOT NULL DEFAULT 0,
                    [ImageUrl] [nvarchar](500) NULL,
                    [Description] [nvarchar](1000) NULL,
                    [Phone] [nvarchar](30) NULL,
                    [Status] [varchar](20) NOT NULL DEFAULT 'Active',
                    [ClosedFrom] [datetime2](0) NULL,
                    [ClosedUntil] [datetime2](0) NULL,
                    [ClosureReason] [nvarchar](500) NULL,
                    [MaintenanceNotice] [nvarchar](1000) NULL,
                    [PartnerId] [int] NULL,
                    [ManagerUserId] [nvarchar](450) NULL,
                    [TimeZoneId] [varchar](60) NOT NULL DEFAULT 'SE Asia Standard Time',
                    [IsDeleted] [bit] NOT NULL DEFAULT 0,
                    [CreatedAt] [datetime2](3) NOT NULL DEFAULT (sysutcdatetime()),
                    [UpdatedAt] [datetime2](3) NULL,
                    [CreatedBy] [nvarchar](450) NULL
                );
                CREATE UNIQUE NONCLUSTERED INDEX [IX_ParkingLots_Code] ON [dbo].[ParkingLots]([Code] ASC);
            END

            -- Ready-made building model the lot's zones, slots and 3D layout are generated from
            IF COL_LENGTH('dbo.ParkingLots', 'LayoutTemplate') IS NULL
            BEGIN
                ALTER TABLE [dbo].[ParkingLots] ADD [LayoutTemplate] [varchar](30) NULL;
            END

            IF OBJECT_ID('dbo.Zones', 'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[Zones](
                    [Id] [int] IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [ParkingLotId] [int] NOT NULL,
                    [Code] [nvarchar](30) NOT NULL,
                    [Name] [nvarchar](200) NOT NULL,
                    [ZoneType] [varchar](20) NOT NULL DEFAULT 'Area',
                    [FloorNo] [int] NULL,
                    [Capacity] [int] NOT NULL DEFAULT 0,
                    [MaxReservationSlots] [int] NULL,
                    [LayoutJson] [nvarchar](max) NULL,
                    [IsActive] [bit] NOT NULL DEFAULT 1,
                    [CreatedAt] [datetime2](3) NOT NULL DEFAULT (sysutcdatetime()),
                    [UpdatedAt] [datetime2](3) NULL,
                    CONSTRAINT [FK_Zones_ParkingLots] FOREIGN KEY([ParkingLotId]) REFERENCES [dbo].[ParkingLots] ([Id])
                );
                CREATE UNIQUE NONCLUSTERED INDEX [IX_Zones_ParkingLotId_Code] ON [dbo].[Zones]([ParkingLotId] ASC, [Code] ASC);
            END
            ELSE
            BEGIN
                IF COL_LENGTH('dbo.Zones', 'MaxReservationSlots') IS NULL
                BEGIN
                    ALTER TABLE [dbo].[Zones] ADD [MaxReservationSlots] [int] NULL;
                END
            END

            IF OBJECT_ID('dbo.ZoneCounters', 'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[ZoneCounters](
                    [Id] [int] IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [ZoneId] [int] NOT NULL,
                    [AvailableSpaces] [int] NOT NULL DEFAULT 0,
                    [OccupiedSpaces] [int] NOT NULL DEFAULT 0,
                    [TotalCapacity] [int] NOT NULL DEFAULT 0,
                    [LastUpdatedAt] [datetime2](3) NOT NULL DEFAULT (sysutcdatetime()),
                    CONSTRAINT [FK_ZoneCounters_Zones] FOREIGN KEY([ZoneId]) REFERENCES [dbo].[Zones] ([Id]) ON DELETE CASCADE
                );
                CREATE UNIQUE NONCLUSTERED INDEX [IX_ZoneCounters_ZoneId] ON [dbo].[ZoneCounters]([ZoneId] ASC);
            END

            IF OBJECT_ID('dbo.Slots', 'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[Slots](
                    [Id] [int] IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [ZoneId] [int] NOT NULL,
                    [VehicleTypeId] [int] NOT NULL,
                    [Code] [nvarchar](30) NOT NULL,
                    [Status] [varchar](20) NOT NULL DEFAULT 'Available',
                    [DedicatedVehicleId] [uniqueidentifier] NULL,
                    [DedicatedNote] [nvarchar](200) NULL,
                    [PosX] [decimal](8,2) NULL,
                    [PosY] [decimal](8,2) NULL,
                    [Width] [decimal](8,2) NULL,
                    [Height] [decimal](8,2) NULL,
                    [IsActive] [bit] NOT NULL DEFAULT 1,
                    [RowVersion] [rowversion] NOT NULL,
                    CONSTRAINT [FK_Slots_Zones] FOREIGN KEY([ZoneId]) REFERENCES [dbo].[Zones] ([Id]),
                    CONSTRAINT [FK_Slots_VehicleTypes] FOREIGN KEY([VehicleTypeId]) REFERENCES [dbo].[VehicleTypes] ([Id])
                );
                CREATE UNIQUE NONCLUSTERED INDEX [IX_Slots_ZoneId_Code] ON [dbo].[Slots]([ZoneId] ASC, [Code] ASC);
            END

            IF OBJECT_ID('dbo.OtpCodes', 'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[OtpCodes](
                    [Id] [bigint] IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [UserId] [nvarchar](450) NULL,
                    [Destination] [nvarchar](256) NOT NULL,
                    [Channel] [varchar](10) NOT NULL,
                    [Purpose] [varchar](30) NOT NULL,
                    [CodeHash] [nvarchar](200) NOT NULL,
                    [AttemptCount] [int] NOT NULL DEFAULT 0,
                    [ExpiresAt] [datetime2](3) NOT NULL,
                    [ConsumedAt] [datetime2](3) NULL,
                    [CreatedAt] [datetime2](3) NOT NULL DEFAULT (sysutcdatetime())
                );
                CREATE NONCLUSTERED INDEX [IX_OtpCodes_Destination_Purpose] ON [dbo].[OtpCodes]([Destination] ASC, [Purpose] ASC);
                CREATE NONCLUSTERED INDEX [IX_OtpCodes_UserId_Purpose] ON [dbo].[OtpCodes]([UserId] ASC, [Purpose] ASC);
            END

            IF OBJECT_ID('dbo.RefreshTokens', 'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[RefreshTokens](
                    [Id] [bigint] IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [UserId] [nvarchar](450) NOT NULL,
                    [TokenHash] [nvarchar](200) NOT NULL,
                    [DeviceInfo] [nvarchar](300) NULL,
                    [IpAddress] [varchar](45) NULL,
                    [CreatedAt] [datetime2](3) NOT NULL DEFAULT (sysutcdatetime()),
                    [ExpiresAt] [datetime2](3) NOT NULL,
                    [RevokedAt] [datetime2](3) NULL,
                    [RevokedReason] [nvarchar](200) NULL,
                    [ReplacedByTokenHash] [nvarchar](200) NULL
                );
                CREATE NONCLUSTERED INDEX [IX_RefreshTokens_UserId] ON [dbo].[RefreshTokens]([UserId] ASC);
                CREATE NONCLUSTERED INDEX [IX_RefreshTokens_ExpiresAt] ON [dbo].[RefreshTokens]([ExpiresAt] ASC);
            END

            IF OBJECT_ID('dbo.AuditLogs', 'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[AuditLogs](
                    [Id] [bigint] IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [UserId] [nvarchar](450) NULL,
                    [UserName] [nvarchar](256) NULL,
                    [Action] [nvarchar](100) NOT NULL,
                    [EntityName] [nvarchar](100) NULL,
                    [EntityId] [nvarchar](100) NULL,
                    [ParkingLotId] [int] NULL,
                    [OldValues] [nvarchar](max) NULL,
                    [NewValues] [nvarchar](max) NULL,
                    [IpAddress] [varchar](45) NULL,
                    [UserAgent] [nvarchar](500) NULL,
                    [CreatedAt] [datetime2](3) NOT NULL DEFAULT (sysutcdatetime())
                );
                CREATE NONCLUSTERED INDEX [IX_AuditLogs_CreatedAt] ON [dbo].[AuditLogs]([CreatedAt] DESC);
            END

            -- Layout 3D (Draft / Published / Archived) and where each slot sits in it (Slot3DMeta)
            IF OBJECT_ID('dbo.LotLayouts', 'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[LotLayouts](
                    [Id] [int] IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [ParkingLotId] [int] NOT NULL,
                    [Version] [int] NOT NULL,
                    [Status] [varchar](20) NOT NULL DEFAULT 'Draft',
                    [TemplateId] [varchar](30) NULL,
                    [FloorsJson] [nvarchar](max) NOT NULL,
                    [CreatedAt] [datetime2](3) NOT NULL DEFAULT (sysutcdatetime()),
                    [CreatedBy] [nvarchar](450) NULL,
                    [UpdatedAt] [datetime2](3) NULL,
                    [PublishedAt] [datetime2](3) NULL,
                    [PublishedBy] [nvarchar](450) NULL,
                    CONSTRAINT [FK_LotLayouts_ParkingLots] FOREIGN KEY([ParkingLotId]) REFERENCES [dbo].[ParkingLots] ([Id])
                );
                CREATE NONCLUSTERED INDEX [IX_LotLayouts_ParkingLotId_Status] ON [dbo].[LotLayouts]([ParkingLotId] ASC, [Status] ASC);
            END

            IF OBJECT_ID('dbo.LotLayoutSlots', 'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[LotLayoutSlots](
                    [Id] [int] IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [LayoutId] [int] NOT NULL,
                    [SlotId] [int] NOT NULL,
                    [FloorNo] [int] NOT NULL,
                    [Row] [int] NOT NULL,
                    [Col] [int] NOT NULL,
                    [Rotation] [int] NOT NULL DEFAULT 0,
                    [ModelUrl] [nvarchar](500) NULL,
                    CONSTRAINT [FK_LotLayoutSlots_LotLayouts] FOREIGN KEY([LayoutId]) REFERENCES [dbo].[LotLayouts] ([Id]) ON DELETE CASCADE,
                    CONSTRAINT [FK_LotLayoutSlots_Slots] FOREIGN KEY([SlotId]) REFERENCES [dbo].[Slots] ([Id])
                );
                CREATE UNIQUE NONCLUSTERED INDEX [IX_LotLayoutSlots_LayoutId_SlotId] ON [dbo].[LotLayoutSlots]([LayoutId] ASC, [SlotId] ASC);
            END

            -- Vehicle paper checks (FR-VER-01..04): cà vẹt + CCCD photos, their OCR result and the Staff decision
            IF OBJECT_ID('dbo.VehicleVerifications', 'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[VehicleVerifications](
                    [Id] [uniqueidentifier] NOT NULL PRIMARY KEY DEFAULT (NEWID()),
                    [VehicleId] [uniqueidentifier] NOT NULL,
                    [CustomerId] [int] NOT NULL,
                    [UserId] [nvarchar](450) NOT NULL,
                    [Status] [varchar](20) NOT NULL,
                    [ResultJson] [nvarchar](max) NULL,
                    [Confidence] [decimal](5,4) NULL,
                    [StatusReason] [nvarchar](500) NULL,
                    [CreatedAt] [datetime2](3) NOT NULL DEFAULT (sysutcdatetime()),
                    [SubmittedAt] [datetime2](3) NULL,
                    [ProcessedAt] [datetime2](3) NULL,
                    [ReviewedBy] [nvarchar](450) NULL,
                    [ReviewNote] [nvarchar](500) NULL,
                    [DecidedAt] [datetime2](3) NULL,
                    [FilesDeletedAt] [datetime2](3) NULL,
                    CONSTRAINT [FK_VehicleVerifications_Vehicles] FOREIGN KEY([VehicleId]) REFERENCES [dbo].[Vehicles] ([Id])
                );
                CREATE NONCLUSTERED INDEX [IX_VehicleVerifications_VehicleId_CreatedAt] ON [dbo].[VehicleVerifications]([VehicleId] ASC, [CreatedAt] ASC);
                CREATE NONCLUSTERED INDEX [IX_VehicleVerifications_Status] ON [dbo].[VehicleVerifications]([Status] ASC);
            END

            IF OBJECT_ID('dbo.VehicleDocuments', 'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[VehicleDocuments](
                    [Id] [uniqueidentifier] NOT NULL PRIMARY KEY DEFAULT (NEWID()),
                    [VerificationId] [uniqueidentifier] NOT NULL,
                    [DocumentType] [varchar](30) NOT NULL,
                    [Side] [varchar](10) NOT NULL,
                    [StoragePath] [nvarchar](500) NULL,
                    [OriginalFileName] [nvarchar](260) NULL,
                    [ContentType] [varchar](50) NOT NULL,
                    [SizeBytes] [bigint] NOT NULL,
                    [UploadedAt] [datetime2](3) NOT NULL DEFAULT (sysutcdatetime()),
                    CONSTRAINT [FK_VehicleDocuments_VehicleVerifications] FOREIGN KEY([VerificationId]) REFERENCES [dbo].[VehicleVerifications] ([Id]) ON DELETE CASCADE
                );
                CREATE NONCLUSTERED INDEX [IX_VehicleDocuments_VerificationId] ON [dbo].[VehicleDocuments]([VerificationId] ASC);
            END
        ";

        await pmsContext.Database.ExecuteSqlRawAsync(sql);
    }

    /// <summary>Check-in / check-out requests from the customer app (dbo.GateRequests). Idempotent.</summary>
    /// <summary>Server-wide settings, one row per key (e.g. the Admin's parking simulation). Idempotent.</summary>
    private static async Task EnsureSystemSettingsTableAsync(PmsDbContext pmsContext)
    {
        await pmsContext.Database.ExecuteSqlRawAsync(@"
            IF OBJECT_ID(N'[dbo].[SystemSettings]', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[SystemSettings](
                    [Id] [int] IDENTITY(1,1) NOT NULL,
                    [SettingKey] [nvarchar](100) NOT NULL,
                    [SettingValue] [nvarchar](max) NULL,
                    [ValueType] [varchar](20) NOT NULL CONSTRAINT [DF_SystemSettings_ValueType] DEFAULT ('string'),
                    [Category] [nvarchar](50) NULL,
                    [Description] [nvarchar](500) NULL,
                    [UpdatedAt] [datetime2](3) NULL,
                    [UpdatedBy] [nvarchar](450) NULL,
                    CONSTRAINT [PK_SystemSettings] PRIMARY KEY CLUSTERED ([Id] ASC)
                );
                CREATE UNIQUE NONCLUSTERED INDEX [UX_SystemSettings_SettingKey] ON [dbo].[SystemSettings]([SettingKey] ASC);
            END");
    }

    // Profile photos live in the database: the hosting disk does not survive a redeploy
    private static async Task EnsureCustomerAvatarsTableAsync(PmsDbContext pmsContext)
    {
        await pmsContext.Database.ExecuteSqlRawAsync(@"
            IF OBJECT_ID(N'[dbo].[CustomerAvatars]', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[CustomerAvatars](
                    [Id] [uniqueidentifier] NOT NULL,
                    [CustomerId] [int] NOT NULL,
                    [Content] [varbinary](max) NOT NULL,
                    [ContentType] [varchar](50) NOT NULL,
                    [CreatedAt] [datetime2](3) NOT NULL,
                    CONSTRAINT [PK_CustomerAvatars] PRIMARY KEY CLUSTERED ([Id] ASC),
                    CONSTRAINT [FK_CustomerAvatars_Customers] FOREIGN KEY ([CustomerId]) REFERENCES [dbo].[Customers]([Id]) ON DELETE CASCADE
                );
                CREATE UNIQUE NONCLUSTERED INDEX [UX_CustomerAvatars_CustomerId] ON [dbo].[CustomerAvatars]([CustomerId] ASC);
            END");
    }

    private static async Task EnsureGateRequestsTableAsync(PmsDbContext pmsContext)
    {
        await pmsContext.Database.ExecuteSqlRawAsync(@"
            IF OBJECT_ID(N'[dbo].[GateRequests]', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[GateRequests](
                    [Id] [bigint] IDENTITY(1,1) NOT NULL,
                    [ParkingLotId] [int] NOT NULL,
                    [Kind] [varchar](20) NOT NULL,
                    [Status] [varchar](20) NOT NULL,
                    [UserId] [nvarchar](450) NULL,
                    [CustomerId] [int] NULL,
                    [ReservationId] [bigint] NULL,
                    [SessionId] [bigint] NULL,
                    [VehicleId] [uniqueidentifier] NULL,
                    [PlateNumber] [nvarchar](20) NULL,
                    [CreatedAt] [datetime2](3) NOT NULL,
                    [HandledAt] [datetime2](3) NULL,
                    [HandledBy] [nvarchar](450) NULL,
                    [Note] [nvarchar](500) NULL,
                    CONSTRAINT [PK_GateRequests] PRIMARY KEY CLUSTERED ([Id] ASC)
                );
                CREATE NONCLUSTERED INDEX [IX_GateRequests_Queue] ON [dbo].[GateRequests]([ParkingLotId] ASC, [Kind] ASC, [Status] ASC, [CreatedAt] ASC);
                CREATE NONCLUSTERED INDEX [IX_GateRequests_UserId] ON [dbo].[GateRequests]([UserId] ASC, [CreatedAt] ASC);
            END;
            -- Simulated walk-ins sent to the entry lane by the Admin parking simulation
            IF COL_LENGTH('dbo.GateRequests', 'IsSimulated') IS NULL ALTER TABLE [dbo].[GateRequests] ADD [IsSimulated] [bit] NOT NULL DEFAULT 0;
            IF COL_LENGTH('dbo.GateRequests', 'VehicleTypeId') IS NULL ALTER TABLE [dbo].[GateRequests] ADD [VehicleTypeId] [int] NULL;
            IF COL_LENGTH('dbo.GateRequests', 'SlotId') IS NULL ALTER TABLE [dbo].[GateRequests] ADD [SlotId] [int] NULL;
            IF COL_LENGTH('dbo.GateRequests', 'SimulatedStaySeconds') IS NULL ALTER TABLE [dbo].[GateRequests] ADD [SimulatedStaySeconds] [int] NULL;");
    }

    /// <summary>Customer notifications shown in the dashboard bell (dbo.UserNotifications). Idempotent.</summary>
    private static async Task EnsureNotificationsTableAsync(PmsDbContext pmsContext)
    {
        await pmsContext.Database.ExecuteSqlRawAsync(@"
            IF OBJECT_ID(N'[dbo].[UserNotifications]', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[UserNotifications](
                    [Id] [bigint] IDENTITY(1,1) NOT NULL,
                    [UserId] [nvarchar](450) NOT NULL,
                    [Type] [varchar](30) NOT NULL,
                    [Title] [nvarchar](200) NOT NULL,
                    [Message] [nvarchar](1000) NOT NULL,
                    [RefType] [varchar](30) NULL,
                    [RefId] [nvarchar](100) NULL,
                    [EventAt] [datetime2](3) NULL,
                    [DataJson] [nvarchar](max) NULL,
                    [CreatedAt] [datetime2](3) NOT NULL DEFAULT (sysutcdatetime()),
                    [ReadAt] [datetime2](3) NULL,
                    CONSTRAINT [PK_UserNotifications] PRIMARY KEY CLUSTERED ([Id] ASC)
                );
                CREATE NONCLUSTERED INDEX [IX_UserNotifications_UserId_CreatedAt] ON [dbo].[UserNotifications]([UserId] ASC, [CreatedAt] DESC);
                CREATE NONCLUSTERED INDEX [IX_UserNotifications_Type_RefId] ON [dbo].[UserNotifications]([Type] ASC, [RefId] ASC);
            END");
    }

    /// <summary>
    /// A rejected registration means the vehicle is not registered. Staff rejections now remove the vehicle
    /// from the fleet; this applies the same rule to vehicles rejected before that (latest check Rejected).
    /// Idempotent: once removed, a vehicle is no longer active.
    /// </summary>
    private static async Task RemoveRejectedVehiclesAsync(PmsDbContext pmsContext)
    {
        await pmsContext.Database.ExecuteSqlRawAsync(@"
            IF OBJECT_ID(N'[dbo].[VehicleVerifications]', N'U') IS NOT NULL
            BEGIN
                UPDATE v SET v.[IsActive] = 0, v.[IsDefault] = 0, v.[UpdatedAt] = SYSUTCDATETIME()
                FROM [dbo].[Vehicles] v
                CROSS APPLY (SELECT TOP 1 vv.[Status] FROM [dbo].[VehicleVerifications] vv
                             WHERE vv.[VehicleId] = v.[Id] ORDER BY vv.[CreatedAt] DESC) latest
                WHERE v.[IsActive] = 1 AND latest.[Status] = 'Rejected';

                -- Customers left without a primary vehicle get their newest one
                UPDATE v SET v.[IsDefault] = 1
                FROM [dbo].[Vehicles] v
                WHERE v.[IsActive] = 1
                  AND NOT EXISTS (SELECT 1 FROM [dbo].[Vehicles] d WHERE d.[CustomerId] = v.[CustomerId] AND d.[IsActive] = 1 AND d.[IsDefault] = 1)
                  AND v.[Id] = (SELECT TOP 1 n.[Id] FROM [dbo].[Vehicles] n WHERE n.[CustomerId] = v.[CustomerId] AND n.[IsActive] = 1 ORDER BY n.[CreatedAt] DESC);
            END");
    }

    private static async Task EnsureWalletTablesAsync(PmsDbContext pmsContext)
    {
        await pmsContext.Database.ExecuteSqlRawAsync(@"
            IF OBJECT_ID('dbo.Wallets', 'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[Wallets](
                    [Id] [int] IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [CustomerId] [int] NOT NULL,
                    [Balance] [decimal](18,2) NOT NULL DEFAULT 0,
                    [Currency] [char](3) NOT NULL DEFAULT 'VND',
                    [Status] [varchar](20) NOT NULL DEFAULT 'Active',
                    [UpdatedAt] [datetime2](3) NOT NULL DEFAULT (sysutcdatetime()),
                    [RowVersion] [rowversion] NOT NULL,
                    CONSTRAINT [FK_Wallets_Customers] FOREIGN KEY([CustomerId]) REFERENCES [dbo].[Customers] ([Id])
                );
                CREATE UNIQUE NONCLUSTERED INDEX [IX_Wallets_CustomerId] ON [dbo].[Wallets]([CustomerId] ASC);
            END

            IF OBJECT_ID('dbo.WalletTransactions', 'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[WalletTransactions](
                    [Id] [bigint] IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [WalletId] [int] NOT NULL,
                    [TxnType] [varchar](20) NOT NULL,
                    [Amount] [decimal](18,2) NOT NULL,
                    [BalanceBefore] [decimal](18,2) NOT NULL DEFAULT 0,
                    [BalanceAfter] [decimal](18,2) NOT NULL DEFAULT 0,
                    [PaymentId] [bigint] NULL,
                    [ReferenceType] [varchar](30) NULL,
                    [ReferenceId] [nvarchar](50) NULL,
                    [Description] [nvarchar](300) NULL,
                    [CreatedAt] [datetime2](3) NOT NULL DEFAULT (sysutcdatetime()),
                    [CreatedBy] [nvarchar](450) NULL,
                    CONSTRAINT [FK_WalletTransactions_Wallets] FOREIGN KEY([WalletId]) REFERENCES [dbo].[Wallets] ([Id])
                );
                CREATE NONCLUSTERED INDEX [IX_WalletTransactions_WalletId] ON [dbo].[WalletTransactions]([WalletId] ASC, [CreatedAt] DESC);
            END");
    }
}
