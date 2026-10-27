using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using PMS.Application.Exceptions;
using PMS.Application.Models.Operations;
using PMS.DataAccess.Persistence.Entities;
using Xunit;

namespace PMS.Application.UnitTests.Operations;

public class StaffHistoryTests
{
    [Fact]
    public async Task History_Returns_Ordered_Sessions_And_Pages_Within_Assigned_Lot()
    {
        var t = await new OperationsTestContext().SeedAsync();
        for (var i = 0; i < 3; i++)
            t.Db.ParkingSessions.Add(new ParkingSession
            {
                SessionCode = $"H-{i}", ParkingLotId = t.Lot.Id, EntryGateId = 1,
                EntryTime = t.Now.AddMinutes(-i), EntryPlateNumber = "51A-123.45",
                EntryPlateNormalized = "51A12345", Status = i == 0 ? SessionStatus.Active : SessionStatus.Completed,
                ExitTime = i == 0 ? null : t.Now, RowVersion = [1]
            });
        await t.Db.SaveChangesAsync();
        var first = await t.Gates.GetHistoryAsync(t.Lot.Id, "51A-123", 1, 2);
        var second = await t.Gates.GetHistoryAsync(t.Lot.Id, "51A-123", 2, 2);
        first.TotalCount.Should().Be(3);
        first.Items.Select(s => s.Code).Should().Equal("H-0", "H-1");
        first.Items[0].ExitTime.Should().BeNull();
        second.Items.Select(s => s.Code).Should().Equal("H-2");
    }

    [Fact]
    public async Task History_Rejects_Staff_Without_Lot_Access()
    {
        var t = await new OperationsTestContext().SeedAsync();
        t.ActAs("other-staff", "Staff");
        await t.Gates.Invoking(g => g.GetHistoryAsync(t.Lot.Id, null, 1, 10))
            .Should().ThrowAsync<ForbiddenException>();
    }
}
