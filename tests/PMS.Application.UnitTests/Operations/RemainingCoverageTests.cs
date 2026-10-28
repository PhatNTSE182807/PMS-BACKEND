using System;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using PMS.Application.Helpers.Parking;
using PMS.Application.Models;
using PMS.Application.Models.Operations;
using PMS.Core.Exceptions;
using PMS.Shared.Services.Impl;
using Xunit;

namespace PMS.Application.UnitTests.Operations;

/// <summary>The last small paths: a cancelled entry, the time zone fallback and a few framework hooks.</summary>
public class RemainingCoverageTests
{
    [Fact]
    public async Task CancelEntryAsync_Should_Cancel_A_Vehicle_That_Turned_Around()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var entry = await t.Gates.CheckInAsync(t.Lot.Id, new CheckInModel { PlateNumber = "30E-111.11", VehicleTypeId = t.TypeId("CAR_4") });

        var cancelled = await t.Gates.CancelEntryAsync(entry.Session.Id);

        cancelled.Status.Should().Be(SessionStatus.Cancelled);
        t.Db.ParkingSessions.Find(entry.Session.Id)!.ClosedNote.Should().Be("Vehicle turned around / cancelled by Staff");
    }

    [Fact]
    public void FirstTimeZone_Should_Fall_Back_To_A_Fixed_UTC_Plus_7_Zone()
    {
        var method = typeof(ParkingText).GetMethod("FirstTimeZone", BindingFlags.NonPublic | BindingFlags.Static)!;

        var zone = (TimeZoneInfo)method.Invoke(null, [new[] { "Nowhere/Unknown", null, " " }])!;

        zone.Id.Should().Be("ICT");
        zone.BaseUtcOffset.Should().Be(TimeSpan.FromHours(7));
    }

    [Fact]
    public void FeeCalculator_Should_Round_To_1000_VND()
    {
        // Read through reflection so the field itself is used, not the value the compiler copied in
        typeof(FeeCalculator).GetField(nameof(FeeCalculator.RoundingStep))!.GetValue(null).Should().Be(1_000m);
        FeeCalculator.RoundUp(1_001).Should().Be(2_000);
    }

    [Fact]
    public void ApiResult_Should_Allow_An_Empty_Instance_For_Deserialisation()
    {
        var empty = (ApiResult<int>)Activator.CreateInstance(typeof(ApiResult<int>), nonPublic: true)!;

        empty.Succeeded.Should().BeFalse();
        empty.Errors.Should().BeNull();
    }

#pragma warning disable SYSLIB0050, SYSLIB0051 // the serialization constructor is kept for older callers
    [Fact]
    public void ResourceNotFoundException_Should_Restore_From_Serialized_Data()
    {
        var info = new SerializationInfo(typeof(ResourceNotFoundException), new FormatterConverter());
        new ResourceNotFoundException("Gone").GetObjectData(info, default);
        var ctor = typeof(ResourceNotFoundException).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, [typeof(SerializationInfo), typeof(StreamingContext)])!;

        var restored = (ResourceNotFoundException)ctor.Invoke([info, default(StreamingContext)]);

        restored.Message.Should().Be("Gone");
    }
#pragma warning restore SYSLIB0050, SYSLIB0051

    [Fact]
    public void ClaimService_Should_Return_Null_Without_An_Http_Context()
    {
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns((HttpContext)null);

        new ClaimService(accessor).GetClaim("role").Should().BeNull();
    }
}
