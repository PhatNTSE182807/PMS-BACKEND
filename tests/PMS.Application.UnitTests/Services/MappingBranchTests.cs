using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using PMS.Application.Exceptions;
using PMS.Application.Helpers;
using PMS.Application.Models.Account;
using PMS.Application.Models.ParkingLot;
using PMS.Application.Models.Pricing;
using PMS.Application.Models.Profile;
using PMS.Application.Models.Slot;
using PMS.Application.Models.Zone;
using PMS.Application.Services;
using PMS.Application.Services.Impl;
using PMS.DataAccess.Identity;
using PMS.DataAccess.Persistence.Entities;
using PMS.Shared.Services.Impl;
using Xunit;

namespace PMS.Application.UnitTests.Services;

/// <summary>
/// Response mapping when related rows are missing (a deleted zone, a user without an employee record...),
/// and small helpers whose edge cases the service tests do not reach.
/// </summary>
public class MappingBranchTests
{
    /// <summary>Calls a private static helper of a service.</summary>
    private static T Call<T>(Type type, string method, params object[] args)
    {
        var info = type.GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)
                   ?? throw new MissingMethodException(type.Name, method);
        try
        {
            return (T)info.Invoke(null, args);
        }
        catch (TargetInvocationException e) when (e.InnerException != null)
        {
            throw e.InnerException;
        }
    }

    [Fact]
    public void Slot_Mapping_Should_Survive_A_Missing_Zone_And_Type()
    {
        var bare = Call<SlotResponseModel>(typeof(SlotService), "MapToResponseModel", new Slot { Id = 1, Code = "A-01" });
        bare.ZoneCode.Should().BeNull();
        bare.ParkingLotId.Should().Be(0);
        bare.ParkingLotCode.Should().BeNull();
        bare.VehicleTypeCode.Should().BeNull();

        var full = Call<SlotResponseModel>(typeof(SlotService), "MapToResponseModel", new Slot
        {
            Code = "A-01",
            Zone = new Zone { Code = "Z1", Name = "Level 1", ParkingLotId = 3, ParkingLot = new ParkingLot { Code = "LOT" } },
            VehicleType = new VehicleType { Code = "CAR_4", Name = "Car" }
        });
        full.ParkingLotId.Should().Be(3);
        full.ParkingLotCode.Should().Be("LOT");
        full.VehicleTypeName.Should().Be("Car");

        var noLot = Call<SlotResponseModel>(typeof(SlotService), "MapToResponseModel", new Slot { Zone = new Zone { ParkingLotId = 4 } });
        noLot.ParkingLotCode.Should().BeNull();
    }

    [Fact]
    public void Zone_Mapping_Should_Survive_A_Missing_Lot_And_Slots()
    {
        var bare = Call<ZoneResponseModel>(typeof(ZoneService), "MapToResponseModel", new Zone { Name = "Z", Slots = null });
        bare.ParkingLotCode.Should().BeNull();
        bare.TotalSlots.Should().Be(0);

        var full = Call<ZoneResponseModel>(typeof(ZoneService), "MapToResponseModel", new Zone
        {
            ParkingLot = new ParkingLot { Code = "LOT", Name = "Central" },
            Slots = new List<Slot> { new(), new() }
        });
        full.ParkingLotName.Should().Be("Central");
        full.TotalSlots.Should().Be(2);
    }

    [Fact]
    public void Employee_Mapping_Should_Fall_Back_To_The_Account_Without_An_Employee_Record()
    {
        var active = Call<EmployeeAccountResponseModel>(typeof(EmployeeAccountService), "ToResponse",
            new ApplicationUser { Id = "u1", UserName = "manager1", IsActive = true }, null, new ParkingLot[] { null });
        active.FullName.Should().Be("manager1");
        active.Status.Should().Be("Active");
        active.EmployeeCode.Should().BeNull();
        active.ParkingLots.Should().BeEmpty();

        var inactive = Call<EmployeeAccountResponseModel>(typeof(EmployeeAccountService), "ToResponse",
            new ApplicationUser { Id = "u2", UserName = "manager2", IsActive = false }, null, Array.Empty<ParkingLot>());
        inactive.Status.Should().Be("Inactive");

        var employee = Call<EmployeeAccountResponseModel>(typeof(EmployeeAccountService), "ToResponse",
            new ApplicationUser { Id = "u3", UserName = "staff1" },
            new Employee { FullName = "Staff One", EmployeeCode = "E1", Status = "Locked", CreatedAt = DateTime.UtcNow },
            new[] { new ParkingLot { Id = 1, Code = "LOT", Name = "Central" } });
        employee.FullName.Should().Be("Staff One");
        employee.Status.Should().Be("Locked");
        employee.ParkingLots.Should().ContainSingle();
    }

    [Fact]
    public void Vehicle_Mapping_Should_Show_An_Unknown_Type()
    {
        Call<VehicleResponseModel>(typeof(UserProfileService), "MapToVehicleResponse", new Vehicle { PlateNumber = "51A" })
            .VehicleType.Should().Be("Unknown");
        Call<VehicleResponseModel>(typeof(UserProfileService), "MapToVehicleResponse", new Vehicle { VehicleType = new VehicleType { Name = "Car" } })
            .VehicleType.Should().Be("Car");
        Call<string>(typeof(UserProfileService), "NormalizePlate", " ").Should().BeEmpty();
        Call<string>(typeof(UserProfileService), "NormalizePlate", "51a-123.45").Should().Be("51A12345");
    }

    [Theory]
    [InlineData("Admin", true)]
    [InlineData("Manager", true)]
    [InlineData("Staff", true)]
    [InlineData("User", false)]
    [InlineData(null, false)]
    public void Internal_Roles_Should_Be_Admin_Manager_And_Staff(string role, bool expected)
    {
        Call<bool>(typeof(UserService), "IsInternalRole", role).Should().Be(expected);
    }

    [Fact]
    public void Lot_And_Pricing_Mapping_Should_Accept_Bare_Rows()
    {
        foreach (var staffed in new[] { true, false })
        {
            var lot = Call<ParkingLotResponseModel>(typeof(ParkingLotService), "MapToResponseModel", new ParkingLot { Status = "Active" }, staffed);
            lot.Should().NotBeNull();
        }

        Call<PricingRuleModel>(typeof(PricingRuleService), "ToModel", new PricingRule { Name = "R" }, null, null).Name.Should().Be("R");
        Call<PricingRuleModel>(typeof(PricingRuleService), "ToModel", new PricingRule { Name = "R", Status = "Active" }, "CAR_4", "Car")
            .VehicleTypeCode.Should().Be("CAR_4");
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("2026-10-01", "2026-10-31")]
    public void Revenue_Date_Range_Should_Accept_Both_Or_Neither(string start, string end)
    {
        var tz = TimeZoneInfo.Utc;
        var range = Call<(DateOnly Start, DateOnly End, DateTime StartUtc, DateTime EndUtc)>(typeof(RevenueReportService), "ResolveDateInterval", start, end, tz);

        range.End.Should().BeOnOrAfter(range.Start);
    }

    [Theory]
    [InlineData("2026-10-01", null, "Both 'startDate' and 'endDate'*")]
    [InlineData(null, "2026-10-01", "Both 'startDate' and 'endDate'*")]
    [InlineData("01/10/2026", "2026-10-31", "Invalid 'startDate'*")]
    [InlineData("2026-10-01", "31/10/2026", "Invalid 'endDate'*")]
    [InlineData("2026-10-31", "2026-10-01", "'endDate' cannot be before*")]
    public void Revenue_Date_Range_Should_Reject_Bad_Input(string start, string end, string message)
    {
        FluentActions.Invoking(() => Call<object>(typeof(RevenueReportService), "ResolveDateInterval", start, end, TimeZoneInfo.Utc))
            .Should().Throw<BadRequestException>().WithMessage(message);
    }

    [Fact]
    public void Inventory_Family_Should_Group_Cars_And_Two_Wheelers()
    {
        ParkingInventory.Family("CAR_7").Should().Be("car");
        ParkingInventory.Family("MOTORBIKE").Should().Be("two-wheel");
        ParkingInventory.Family("BICYCLE").Should().Be("BICYCLE");
        ParkingInventory.Family(null).Should().BeEmpty();

        var types = new Dictionary<int, VehicleType>
        {
            [1] = new() { Code = "CAR_4" }, [2] = new() { Code = "CAR_7" }, [3] = new() { Code = "MOTORBIKE" }
        };
        ParkingInventory.Compatible(types, 1, 1).Should().BeTrue();
        ParkingInventory.Compatible(types, 1, 2).Should().BeTrue();
        ParkingInventory.Compatible(types, 1, 3).Should().BeFalse();
        ParkingInventory.Compatible(types, 9, 1).Should().BeFalse();
        ParkingInventory.Compatible(types, 1, 9).Should().BeFalse();
    }

    [Fact]
    public void JwtHelper_Should_Sign_Accounts_Without_Name_Phone_Or_Role()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string> { ["JwtConfiguration:SecretKey"] = "unit-test-secret-key-that-is-long-enough-for-hmac" })
            .Build();

        JwtHelper.GenerateToken(new ApplicationUser { Id = "u1", Role = null }, config).Should().NotBeNullOrEmpty();
        JwtHelper.GenerateToken(new ApplicationUser { Id = "u2", UserName = "a", Email = "a@example.com", PhoneNumber = "0912345678", Role = "User" }, config)
            .Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void ClaimService_Should_Handle_A_Request_Without_A_User()
    {
        var context = Substitute.For<HttpContext>();
        context.User.Returns((ClaimsPrincipal)null);
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(context);

        new ClaimService(accessor).GetUserId().Should().BeNull();
    }

    [Fact]
    public void LocalVehicleDocumentStorage_Should_Accept_Absolute_Roots_And_Refuse_Escapes()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pms-storage-{Guid.NewGuid():N}");
        try
        {
            var storage = new LocalVehicleDocumentStorage(new VehicleDocumentStorageOptions { StorageRoot = root }, "ignored");
            var relative = new LocalVehicleDocumentStorage(new VehicleDocumentStorageOptions { StorageRoot = "docs" }, root);

            storage.OpenRead(null).Should().BeNull();
            storage.OpenRead("missing/file.bin").Should().BeNull();
            storage.Delete(null);
            storage.Delete("missing/file.bin");
            relative.OpenRead("x.bin").Should().BeNull();
            FluentActions.Invoking(() => storage.OpenRead("../outside.bin")).Should().Throw<InvalidOperationException>();

            // A folder that still holds another file is kept
            var first = storage.SaveAsync(new MemoryStream([1]), "same").GetAwaiter().GetResult();
            var second = storage.SaveAsync(new MemoryStream([2]), "same").GetAwaiter().GetResult();
            storage.Delete(first);
            Directory.Exists(Path.Combine(root, "same")).Should().BeTrue();
            storage.Delete(second);
            Directory.Exists(Path.Combine(root, "same")).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }
}
