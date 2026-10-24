using System.Net;
using System.Threading.Tasks;
using FluentAssertions;
using PMS.API;
using PMS.Api.IntegrationTests.Common;
using PMS.Api.IntegrationTests.Helpers;
using PMS.Application.Models.Operations;
using Xunit;

namespace PMS.Api.IntegrationTests.Tests;

/// <summary>The development / testing switch for simulated VNPay and MoMo payments.</summary>
public class PaymentSimulationEndpointTests
{
    private readonly ApiApplicationFactory<Program> _factory = new();

    [Fact]
    public async Task Testing_Environment_Allows_Simulated_Payments()
    {
        var response = await _factory.CreateUnauthorizedClient().GetAsync("/api/payments/simulation");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ResponseHelper.GetApiResultAsync<PaymentSimulationModel>(response)).Result.Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task Simulating_A_Payment_Needs_A_Signed_In_User()
    {
        var response = await _factory.CreateUnauthorizedClient().PostAsync("/api/payments/1/simulate-success", null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
