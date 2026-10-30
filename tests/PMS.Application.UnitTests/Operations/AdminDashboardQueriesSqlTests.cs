using System;
using System.Linq;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PMS.Application.Services.Impl;
using PMS.DataAccess.Persistence;
using Xunit;
using Xunit.Abstractions;

namespace PMS.Application.UnitTests.Operations;

/// <summary>
/// The in-memory provider runs any LINQ, even a query SQL Server could never translate. These tests build the
/// dashboard queries on the SQL Server provider (no connection is opened) and read the SQL, so a query that stops
/// translating, or turns into one statement per lot, fails here instead of in production.
/// </summary>
public class AdminDashboardQueriesSqlTests : IDisposable
{
    private readonly PmsDbContext _db;
    private readonly ITestOutputHelper _output;

    public AdminDashboardQueriesSqlTests(ITestOutputHelper output)
    {
        _output = output;
        var options = new DbContextOptionsBuilder<PmsDbContext>()
            .UseSqlServer("Server=.;Database=sql_translation_check;Trusted_Connection=True;TrustServerCertificate=True;")
            .Options;
        _db = new PmsDbContext(options);
    }

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    private static int Count(string sql, string token) =>
        sql.Split(token, StringSplitOptions.None).Length - 1;

    [Fact]
    public void BaysFromCounters_IsOneGroupedStatement_ForAllLots()
    {
        var sql = _db.BaysFromCounters().ToQueryString();
        _output.WriteLine(sql);

        sql.Should().Contain("GROUP BY");
        Count(sql, "SUM(").Should().Be(2);
        Count(sql, "SELECT").Should().Be(1, "a single flat SELECT: no correlated subquery per lot");
        sql.Should().NotContain("@__lotId", "bays are never fetched one lot at a time");
    }

    [Fact]
    public void BaysFromSlotsWithoutCounter_IsOneGroupedStatement_ForAllLots()
    {
        var sql = _db.BaysFromSlotsWithoutCounter().ToQueryString();
        _output.WriteLine(sql);

        sql.Should().Contain("GROUP BY");
        sql.Should().Contain("LEFT JOIN [ZoneCounters]");
        sql.Should().NotContain("@__lotId");
    }

    [Fact]
    public void RevenueByLot_IsOneGroupedStatement_WithTodayAndMonthSums()
    {
        var from = new DateTime(2026, 9, 30, 17, 0, 0, DateTimeKind.Utc);
        var sql = _db.RevenueByLot(from, from.AddDays(28), from.AddDays(29)).ToQueryString();
        _output.WriteLine(sql);

        sql.Should().Contain("GROUP BY");
        sql.Should().Contain("SUM(CASE", "the today sum is a conditional SUM, not a second query");
        Count(sql, "SUM(").Should().Be(2);
    }

    [Fact]
    public void SettledSessionsOfLiveLots_FilterInSql_NotInMemory()
    {
        var sql = _db.SettledSessionsOfLiveLots().Select(s => s.FinalFee).ToQueryString();
        _output.WriteLine(sql);

        sql.Should().Contain("ParkingSessions");
        sql.Should().Contain("IsDeleted");
    }
}
