using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace PMS.DataAccess.Persistence;

public static partial class AutomatedMigration
{
    /// <summary>Customers' account deletion requests (dbo.AccountDeletionRequests). Idempotent.</summary>
    private static async Task EnsureAccountDeletionRequestsTableAsync(PmsDbContext pmsContext)
    {
        await pmsContext.Database.ExecuteSqlRawAsync(@"
            IF OBJECT_ID(N'[dbo].[AccountDeletionRequests]', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[AccountDeletionRequests](
                    [Id] [bigint] IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [UserId] [nvarchar](450) NOT NULL,
                    [Reason] [nvarchar](500) NULL,
                    [Status] [varchar](20) NOT NULL,
                    [RequestedAt] [datetime2](3) NOT NULL,
                    [ClosedBy] [nvarchar](450) NULL,
                    [ClosedAt] [datetime2](3) NULL
                );
                CREATE NONCLUSTERED INDEX [IX_AccountDeletionRequests_User_Status] ON [dbo].[AccountDeletionRequests]([UserId] ASC, [Status] ASC);
            END");
    }
}
