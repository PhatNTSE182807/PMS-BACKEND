using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using PMS.Application.Exceptions;
using PMS.Application.Services.Impl;
using Xunit;

namespace PMS.Application.UnitTests.Services;

public class GoogleTokenValidatorTests
{
    private static GoogleTokenValidator CreateSut(string clientId) =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string> { { "GoogleAuth:ClientId", clientId } })
            .Build());

    [Fact]
    public async Task ValidateAsync_Should_Throw_When_Client_Id_Is_Not_Configured()
    {
        // Arrange
        var sut = CreateSut("");

        // Act
        Func<Task> act = () => sut.ValidateAsync("any-token");

        // Assert
        await act.Should().ThrowAsync<BadRequestException>().WithMessage("Google sign-in is not configured on the server.");
    }

    [Fact]
    public async Task ValidateAsync_Should_Reject_A_Malformed_Token()
    {
        // Arrange
        var sut = CreateSut("1234-test.apps.googleusercontent.com");

        // Act
        Func<Task> act = () => sut.ValidateAsync("not-a-jwt");

        // Assert
        await act.Should().ThrowAsync<BadRequestException>().WithMessage("Google sign-in failed. Please try again.");
    }
}
