using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace PMS.DataAccess.Persistence;

public static partial class AutomatedMigration
{
    /// <summary>Price changes a Manager saved as drafts (dbo.PricingRuleDrafts). Idempotent.</summary>
    private static async Task EnsurePricingRuleDraftsTableAsync(PmsDbContext pmsContext)
    {
        await pmsContext.Database.ExecuteSqlRawAsync(@"
            IF OBJECT_ID(N'[dbo].[PricingRuleDrafts]', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[PricingRuleDrafts](
                    [Id] [int] IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [PricingRuleId] [int] NOT NULL,
                    [ParkingLotId] [int] NOT NULL,
                    [ValuesJson] [nvarchar](max) NOT NULL,
                    [Note] [nvarchar](500) NULL,
                    [BaseVersionNo] [int] NOT NULL,
                    [Status] [varchar](20) NOT NULL,
                    [CreatedBy] [nvarchar](450) NULL,
                    [CreatedAt] [datetime2](3) NOT NULL,
                    [UpdatedBy] [nvarchar](450) NULL,
                    [UpdatedAt] [datetime2](3) NULL,
                    [ClosedBy] [nvarchar](450) NULL,
                    [ClosedAt] [datetime2](3) NULL,
                    CONSTRAINT [FK_PricingRuleDrafts_PricingRules] FOREIGN KEY([PricingRuleId]) REFERENCES [dbo].[PricingRules] ([Id]),
                    CONSTRAINT [FK_PricingRuleDrafts_ParkingLots] FOREIGN KEY([ParkingLotId]) REFERENCES [dbo].[ParkingLots] ([Id])
                );
                CREATE NONCLUSTERED INDEX [IX_PricingRuleDrafts_Lot_Status] ON [dbo].[PricingRuleDrafts]([ParkingLotId] ASC, [Status] ASC);
            END");
    }
}
