using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PMS.DataAccess.Identity;

namespace PMS.DataAccess.Persistence;

public static class AutomatedMigration
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
                    ('MOTORBIKE', 'Motorbike', 'Two-wheeled motorcycles and scooters', 3, 1),
                    ('BICYCLE', 'Bicycle', 'Bicycles and light electric bikes', 4, 1);
            END

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
        ";

        await pmsContext.Database.ExecuteSqlRawAsync(sql);
    }
}
