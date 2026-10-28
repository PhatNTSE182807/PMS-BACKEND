using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using PMS.Core.Common;
using PMS.Core.Exceptions;
using PMS.DataAccess.Identity;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Repositories.Impl;
using PMS.Shared.Services;
using Xunit;

namespace PMS.Application.UnitTests.DataAccess;

/// <summary>An audited entity used only by these tests.</summary>
public class TestNote : BaseEntity, IAuditedEntity
{
    public string Text { get; set; }
    public string CreatedBy { get; set; }
    public DateTime CreatedOn { get; set; }
    public string UpdatedBy { get; set; }
    public DateTime? UpdatedOn { get; set; }
}

/// <summary>The Identity context plus a table of test notes.</summary>
public class TestDatabaseContext(DbContextOptions<DatabaseContext> options, IClaimService claims) : DatabaseContext(options, claims)
{
    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<TestNote>();
    }
}

public class TestNoteRepository(DatabaseContext context) : BaseRepository<TestNote>(context);

/// <summary>The generic repository, the audited Identity context and its configuration.</summary>
public class DataAccessTests : IDisposable
{
    private readonly IClaimService _claims = Substitute.For<IClaimService>();
    private readonly TestDatabaseContext _context;
    private readonly TestNoteRepository _repository;

    public DataAccessTests()
    {
        _claims.GetUserId().Returns("user-1");
        _context = new TestDatabaseContext(
            new DbContextOptionsBuilder<DatabaseContext>().UseInMemoryDatabase($"DataAccessTests_{Guid.NewGuid()}").Options,
            _claims);
        _repository = new TestNoteRepository(_context);
    }

    public void Dispose() => _context.Dispose();

    [Fact]
    public async Task AddAsync_Should_Save_And_Stamp_The_Creator()
    {
        var note = await _repository.AddAsync(new TestNote { Text = "hello" });

        note.CreatedBy.Should().Be("user-1");
        note.CreatedOn.Should().BeCloseTo(DateTime.Now, TimeSpan.FromMinutes(1));
        note.UpdatedBy.Should().BeNull();
        _context.Set<TestNote>().Should().ContainSingle();
    }

    [Fact]
    public async Task UpdateAsync_Should_Stamp_The_Editor()
    {
        var note = await _repository.AddAsync(new TestNote { Text = "hello" });
        _claims.GetUserId().Returns("user-2");
        note.Text = "changed";

        var updated = await _repository.UpdateAsync(note);

        updated.UpdatedBy.Should().Be("user-2");
        updated.UpdatedOn.Should().NotBeNull();
        updated.CreatedBy.Should().Be("user-1");
    }

    [Fact]
    public async Task SaveChangesAsync_Should_Leave_Unchanged_And_Deleted_Rows_Alone()
    {
        var note = await _repository.AddAsync(new TestNote { Text = "hello" });
        _claims.GetUserId().Returns("user-2");

        await _context.SaveChangesAsync();
        note.UpdatedBy.Should().BeNull();

        var removed = await _repository.DeleteAsync(note);

        removed.UpdatedBy.Should().BeNull();
        _context.Set<TestNote>().Should().BeEmpty();
    }

    [Fact]
    public async Task GetAllAsync_And_GetFirstAsync_Should_Filter()
    {
        await _repository.AddAsync(new TestNote { Text = "a" });
        await _repository.AddAsync(new TestNote { Text = "b" });
        await _repository.AddAsync(new TestNote { Text = "b" });

        (await _repository.GetAllAsync(n => n.Text == "b")).Should().HaveCount(2);
        (await _repository.GetFirstAsync(n => n.Text == "a")).Text.Should().Be("a");
    }

    [Fact]
    public async Task GetFirstAsync_Should_Throw_When_Nothing_Matches()
    {
        await FluentActions.Invoking(() => _repository.GetFirstAsync(n => n.Text == "missing"))
            .Should().ThrowAsync<ResourceNotFoundException>().WithMessage("*TestNote is missing");
    }

    [Fact]
    public void Model_Should_Map_Users_To_The_Existing_Users_Table()
    {
        var user = _context.Model.FindEntityType(typeof(ApplicationUser))!;

        user.GetTableName().Should().Be("Users");
        user.FindProperty(nameof(ApplicationUser.Id))!.GetColumnName().Should().Be("UserId");
        user.FindProperty(nameof(ApplicationUser.PasswordHash))!.GetColumnName().Should().Be("Password");
        user.FindProperty(nameof(ApplicationUser.Role))!.GetMaxLength().Should().Be(50);
        user.FindProperty(nameof(ApplicationUser.Role))!.GetDefaultValue().Should().Be("User");
        user.FindProperty(nameof(ApplicationUser.IsActive))!.GetDefaultValue().Should().Be(true);
        user.FindProperty(nameof(ApplicationUser.UserId)).Should().BeNull("UserId is only an alias of Id");
    }

    [Fact]
    public void ApplicationUser_UserId_Should_Alias_Id()
    {
        var user = new ApplicationUser { UserId = "abc" };

        user.Id.Should().Be("abc");
        user.UserId.Should().Be("abc");
        user.Role.Should().Be("User");
        user.IsActive.Should().BeTrue();
    }

    [Fact]
    public void Password_Reset_Links_Should_Last_30_Minutes()
    {
        var options = new PasswordResetTokenProviderOptions();

        options.Name.Should().Be("PasswordResetDataProtectorTokenProvider");
        options.TokenLifespan.Should().Be(TimeSpan.FromMinutes(30));
        PasswordResetTokenProviderOptions.ProviderName.Should().Be("PasswordReset");

        var provider = new PasswordResetTokenProvider(
            new EphemeralDataProtectionProvider(),
            Options.Create(options),
            Substitute.For<ILogger<DataProtectorTokenProvider<ApplicationUser>>>());

        provider.Name.Should().Be("PasswordResetDataProtectorTokenProvider");
    }
}
