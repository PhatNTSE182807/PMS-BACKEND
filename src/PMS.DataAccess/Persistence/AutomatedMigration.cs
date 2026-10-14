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
        ";

        await pmsContext.Database.ExecuteSqlRawAsync(sql);
    }
}
