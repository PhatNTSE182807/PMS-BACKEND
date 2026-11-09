using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.DataAccess.Persistence;

public partial class PmsDbContext : DbContext
{
    public PmsDbContext(DbContextOptions<PmsDbContext> options)
        : base(options)
    {
    }

    public virtual DbSet<AccessCard> AccessCards { get; set; }

    public virtual DbSet<ApiKey> ApiKeys { get; set; }

    public virtual DbSet<AspNetRole> AspNetRoles { get; set; }

    public virtual DbSet<AspNetRoleClaim> AspNetRoleClaims { get; set; }

    public virtual DbSet<AspNetUserClaim> AspNetUserClaims { get; set; }

    public virtual DbSet<AspNetUserLogin> AspNetUserLogins { get; set; }

    public virtual DbSet<AspNetUserToken> AspNetUserTokens { get; set; }

    public virtual DbSet<AuditLog> AuditLogs { get; set; }

    public virtual DbSet<BackupJob> BackupJobs { get; set; }

    public virtual DbSet<BarrierControlLog> BarrierControlLogs { get; set; }

    public virtual DbSet<CorporateAccount> CorporateAccounts { get; set; }

    public virtual DbSet<Customer> Customers { get; set; }

    public virtual DbSet<CustomerFeedback> CustomerFeedbacks { get; set; }

    public virtual DbSet<DataDeletionRequest> DataDeletionRequests { get; set; }

    public virtual DbSet<Device> Devices { get; set; }

    public virtual DbSet<DeviceEvent> DeviceEvents { get; set; }

    public virtual DbSet<Einvoice> Einvoices { get; set; }

    public virtual DbSet<Employee> Employees { get; set; }

    public virtual DbSet<EmployeeAssignment> EmployeeAssignments { get; set; }

    public virtual DbSet<EmployeePerformanceReview> EmployeePerformanceReviews { get; set; }

    public virtual DbSet<Gate> Gates { get; set; }

    public virtual DbSet<Holiday> Holidays { get; set; }

    public virtual DbSet<Integration> Integrations { get; set; }

    public virtual DbSet<Invoice> Invoices { get; set; }

    public virtual DbSet<LinkedPaymentAccount> LinkedPaymentAccounts { get; set; }

    public virtual DbSet<LotReview> LotReviews { get; set; }

    public virtual DbSet<LoyaltyTransaction> LoyaltyTransactions { get; set; }

    public virtual DbSet<OtpCode> OtpCodes { get; set; }

    public virtual DbSet<ParkingLot> ParkingLots { get; set; }

    public virtual DbSet<ParkingLotImage> ParkingLotImages { get; set; }

    public virtual DbSet<ParkingSession> ParkingSessions { get; set; }

    public virtual DbSet<Partner> Partners { get; set; }

    public virtual DbSet<Payment> Payments { get; set; }

    public virtual DbSet<Permission> Permissions { get; set; }

    public virtual DbSet<PricingRule> PricingRules { get; set; }

    public virtual DbSet<PricingRuleHistory> PricingRuleHistories { get; set; }

    public virtual DbSet<PricingTimeBand> PricingTimeBands { get; set; }

    public virtual DbSet<Promotion> Promotions { get; set; }

    public virtual DbSet<RefreshToken> RefreshTokens { get; set; }

    public virtual DbSet<Reservation> Reservations { get; set; }

    public virtual DbSet<RolePermission> RolePermissions { get; set; }

    public virtual DbSet<SessionViolation> SessionViolations { get; set; }

    public virtual DbSet<Shift> Shifts { get; set; }

    public virtual DbSet<ShiftCashCount> ShiftCashCounts { get; set; }

    public virtual DbSet<ShiftSchedule> ShiftSchedules { get; set; }

    public virtual DbSet<ShiftTemplate> ShiftTemplates { get; set; }

    public virtual DbSet<Slot> Slots { get; set; }

    public virtual DbSet<Subscription> Subscriptions { get; set; }

    public virtual DbSet<SubscriptionPlan> SubscriptionPlans { get; set; }

    public virtual DbSet<SubscriptionRenewal> SubscriptionRenewals { get; set; }

    public virtual DbSet<SystemSetting> SystemSettings { get; set; }

    public virtual DbSet<CustomerAvatar> CustomerAvatars { get; set; }

    public virtual DbSet<User> Users { get; set; }

    public virtual DbSet<UserConsent> UserConsents { get; set; }

    public virtual DbSet<UserLotAccess> UserLotAccesses { get; set; }

    public virtual DbSet<Vehicle> Vehicles { get; set; }

    public virtual DbSet<VehicleType> VehicleTypes { get; set; }

    public virtual DbSet<Voucher> Vouchers { get; set; }

    public virtual DbSet<Wallet> Wallets { get; set; }

    public virtual DbSet<WalletTransaction> WalletTransactions { get; set; }

    public virtual DbSet<WebhookEndpoint> WebhookEndpoints { get; set; }

    public virtual DbSet<Zone> Zones { get; set; }

    public virtual DbSet<ZoneCounter> ZoneCounters { get; set; }

    public virtual DbSet<ZoneOccupancy> ZoneOccupancies { get; set; }

    public virtual DbSet<ZoneVehicleCapacity> ZoneVehicleCapacities { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AccessCard>(entity =>
        {
            entity.Property(e => e.CardNumber)
                .IsRequired()
                .HasMaxLength(50);
            entity.Property(e => e.CardType)
                .IsRequired()
                .HasMaxLength(20)
                .IsUnicode(false);
            entity.Property(e => e.CreatedAt)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.ExpiresAt).HasPrecision(0);
            entity.Property(e => e.IssuedAt).HasPrecision(0);
            entity.Property(e => e.Note).HasMaxLength(300);
            entity.Property(e => e.Status)
                .IsRequired()
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasDefaultValue("Available");
        });

        modelBuilder.Entity<ApiKey>(entity =>
        {
            entity.Property(e => e.CreatedAt)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.CreatedBy).HasMaxLength(450);
            entity.Property(e => e.ExpiresAt).HasPrecision(3);
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.KeyHash)
                .IsRequired()
                .HasMaxLength(200);
            entity.Property(e => e.KeyPrefix)
                .IsRequired()
                .HasMaxLength(12)
                .IsUnicode(false);
            entity.Property(e => e.LastUsedAt).HasPrecision(3);
            entity.Property(e => e.Name)
                .IsRequired()
                .HasMaxLength(200);
            entity.Property(e => e.RevokedAt).HasPrecision(3);
            entity.Property(e => e.Scopes).HasMaxLength(500);
        });

        modelBuilder.Entity<AspNetRole>(entity =>
        {
            entity.HasIndex(e => e.NormalizedName, "RoleNameIndex")
                .IsUnique()
                .HasFilter("([NormalizedName] IS NOT NULL)");

            entity.Property(e => e.Name).HasMaxLength(256);
            entity.Property(e => e.NormalizedName).HasMaxLength(256);
        });

        modelBuilder.Entity<AspNetRoleClaim>(entity =>
        {
            entity.HasIndex(e => e.RoleId, "IX_AspNetRoleClaims_RoleId");

            entity.Property(e => e.RoleId).IsRequired();

            entity.HasOne(d => d.Role).WithMany(p => p.AspNetRoleClaims).HasForeignKey(d => d.RoleId);
        });

        modelBuilder.Entity<AspNetUserClaim>(entity =>
        {
            entity.HasIndex(e => e.UserId, "IX_AspNetUserClaims_UserId");

            entity.Property(e => e.UserId).IsRequired();

            entity.HasOne(d => d.User).WithMany(p => p.AspNetUserClaims).HasForeignKey(d => d.UserId);
        });

        modelBuilder.Entity<AspNetUserLogin>(entity =>
        {
            entity.HasKey(e => new { e.LoginProvider, e.ProviderKey });

            entity.HasIndex(e => e.UserId, "IX_AspNetUserLogins_UserId");

            entity.Property(e => e.LoginProvider).HasMaxLength(128);
            entity.Property(e => e.ProviderKey).HasMaxLength(128);
            entity.Property(e => e.UserId).IsRequired();

            entity.HasOne(d => d.User).WithMany(p => p.AspNetUserLogins).HasForeignKey(d => d.UserId);
        });

        modelBuilder.Entity<AspNetUserToken>(entity =>
        {
            entity.HasKey(e => new { e.UserId, e.LoginProvider, e.Name });

            entity.Property(e => e.LoginProvider).HasMaxLength(128);
            entity.Property(e => e.Name).HasMaxLength(128);

            entity.HasOne(d => d.User).WithMany(p => p.AspNetUserTokens).HasForeignKey(d => d.UserId);
        });

        modelBuilder.Entity<AuditLog>(entity =>
        {
            entity.HasIndex(e => e.CreatedAt, "IX_AuditLogs_CreatedAt").IsDescending();

            entity.Property(e => e.Action)
                .IsRequired()
                .HasMaxLength(100);
            entity.Property(e => e.CreatedAt)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.EntityId).HasMaxLength(100);
            entity.Property(e => e.EntityName).HasMaxLength(100);
            entity.Property(e => e.IpAddress)
                .HasMaxLength(45)
                .IsUnicode(false);
            entity.Property(e => e.UserAgent).HasMaxLength(500);
            entity.Property(e => e.UserId).HasMaxLength(450);
            entity.Property(e => e.UserName).HasMaxLength(256);
        });

        modelBuilder.Entity<BackupJob>(entity =>
        {
            entity.Property(e => e.FilePath).HasMaxLength(500);
            entity.Property(e => e.FinishedAt).HasPrecision(3);
            entity.Property(e => e.JobType)
                .IsRequired()
                .HasMaxLength(20)
                .IsUnicode(false);
            entity.Property(e => e.StartedAt)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.Status)
                .IsRequired()
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasDefaultValue("Running");
            entity.Property(e => e.TriggeredBy).HasMaxLength(450);
        });

        modelBuilder.Entity<BarrierControlLog>(entity =>
        {
            entity.Property(e => e.Command)
                .IsRequired()
                .HasMaxLength(10)
                .IsUnicode(false);
            entity.Property(e => e.CreatedAt)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.Reason).HasMaxLength(500);
            entity.Property(e => e.Source)
                .IsRequired()
                .HasMaxLength(10)
                .IsUnicode(false);
            entity.Property(e => e.Succeeded).HasDefaultValue(true);
            entity.Property(e => e.UserId).HasMaxLength(450);
        });

        modelBuilder.Entity<CorporateAccount>(entity =>
        {
            entity.Property(e => e.Address).HasMaxLength(500);
            entity.Property(e => e.BillingEmail).HasMaxLength(256);
            entity.Property(e => e.CompanyName)
                .IsRequired()
                .HasMaxLength(200);
            entity.Property(e => e.ContactName).HasMaxLength(200);
            entity.Property(e => e.CreatedAt)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.Email).HasMaxLength(256);
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.Phone).HasMaxLength(30);
            entity.Property(e => e.TaxCode).HasMaxLength(30);
            entity.Property(e => e.UpdatedAt).HasPrecision(3);
        });

        modelBuilder.Entity<Customer>(entity =>
        {
            entity.Property(e => e.Address).HasMaxLength(500);
            entity.Property(e => e.CitizenId).HasMaxLength(12).IsUnicode(false);
            entity.Property(e => e.AnonymizedAt).HasPrecision(3);
            entity.Property(e => e.CreatedAt)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.CustomerCode)
                .IsRequired()
                .HasMaxLength(30);
            entity.Property(e => e.CustomerType)
                .IsRequired()
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasDefaultValue("Individual");
            entity.Property(e => e.Email).HasMaxLength(256);
            entity.Property(e => e.AvatarUrl).HasMaxLength(500);
            entity.Property(e => e.FullName)
                .IsRequired()
                .HasMaxLength(200);
            entity.Property(e => e.Gender)
                .HasMaxLength(10)
                .IsUnicode(false);
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.MembershipTier)
                .IsRequired()
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasDefaultValue("Standard");
            entity.Property(e => e.Phone).HasMaxLength(30);
            entity.Property(e => e.UpdatedAt).HasPrecision(3);
            entity.Property(e => e.UserId).HasMaxLength(450);
        });

        modelBuilder.Entity<CustomerFeedback>(entity =>
        {
            entity.Property(e => e.AssignedTo).HasMaxLength(450);
            entity.Property(e => e.Content).IsRequired();
            entity.Property(e => e.CreatedAt)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.FeedbackType)
                .IsRequired()
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasDefaultValue("Feedback");
            entity.Property(e => e.RespondedAt).HasPrecision(3);
            entity.Property(e => e.Status)
                .IsRequired()
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasDefaultValue("New");
            entity.Property(e => e.Subject)
                .IsRequired()
                .HasMaxLength(200);
        });

        modelBuilder.Entity<DataDeletionRequest>(entity =>
        {
            entity.Property(e => e.Note).HasMaxLength(1000);
            entity.Property(e => e.ProcessedAt).HasPrecision(3);
            entity.Property(e => e.ProcessedBy).HasMaxLength(450);
            entity.Property(e => e.RequestedAt)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.Status)
                .IsRequired()
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasDefaultValue("Pending");
            entity.Property(e => e.UserId)
                .IsRequired()
                .HasMaxLength(450);
        });

        modelBuilder.Entity<Device>(entity =>
        {
            entity.Property(e => e.Brand).HasMaxLength(100);
            entity.Property(e => e.Code)
                .IsRequired()
                .HasMaxLength(50);
            entity.Property(e => e.CreatedAt)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.DeviceType)
                .IsRequired()
                .HasMaxLength(30)
                .IsUnicode(false);
            entity.Property(e => e.FirmwareVersion).HasMaxLength(50);
            entity.Property(e => e.IpAddress)
                .HasMaxLength(45)
                .IsUnicode(false);
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.LastHeartbeatAt).HasPrecision(3);
            entity.Property(e => e.Model).HasMaxLength(100);
            entity.Property(e => e.Name)
                .IsRequired()
                .HasMaxLength(200);
            entity.Property(e => e.Protocol)
                .HasMaxLength(20)
                .IsUnicode(false);
            entity.Property(e => e.SerialNumber).HasMaxLength(100);
            entity.Property(e => e.Status)
                .IsRequired()
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasDefaultValue("Offline");
            entity.Property(e => e.UpdatedAt).HasPrecision(3);

            entity.HasOne(d => d.Gate).WithMany(p => p.Devices)
                .HasForeignKey(d => d.GateId)
                .HasConstraintName("FK_Devices_Gates");
        });

        modelBuilder.Entity<DeviceEvent>(entity =>
        {
            entity.Property(e => e.EventType)
                .IsRequired()
                .HasMaxLength(50)
                .IsUnicode(false);
            entity.Property(e => e.Message).HasMaxLength(1000);
            entity.Property(e => e.OccurredAt)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.Severity)
                .IsRequired()
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasDefaultValue("Info");
        });

        modelBuilder.Entity<Einvoice>(entity =>
        {
            entity.ToTable("EInvoices");

            entity.Property(e => e.CreatedAt)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.EinvoiceNumber)
                .HasMaxLength(50)
                .HasColumnName("EInvoiceNumber");
            entity.Property(e => e.IssuedAt).HasPrecision(3);
            entity.Property(e => e.PdfUrl).HasMaxLength(500);
            entity.Property(e => e.Provider)
                .IsRequired()
                .HasMaxLength(30)
                .IsUnicode(false);
            entity.Property(e => e.Series).HasMaxLength(20);
            entity.Property(e => e.Status)
                .IsRequired()
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasDefaultValue("Pending");
            entity.Property(e => e.TemplateCode).HasMaxLength(50);
            entity.Property(e => e.TransactionUuid).HasMaxLength(100);
        });

        modelBuilder.Entity<Employee>(entity =>
        {
            entity.Property(e => e.CreatedAt)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.Email).HasMaxLength(256);
            entity.Property(e => e.EmployeeCode)
                .IsRequired()
                .HasMaxLength(30);
            entity.Property(e => e.FullName)
                .IsRequired()
                .HasMaxLength(200);
            entity.Property(e => e.Phone).HasMaxLength(30);
            entity.Property(e => e.Position)
                .IsRequired()
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasDefaultValue("Attendant");
            entity.Property(e => e.Status)
                .IsRequired()
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasDefaultValue("Active");
            entity.Property(e => e.UpdatedAt).HasPrecision(3);
            entity.Property(e => e.UserId)
                .IsRequired()
                .HasMaxLength(450);

            entity.HasIndex(e => e.EmployeeCode).IsUnique();
            entity.HasIndex(e => e.UserId);
        });

        modelBuilder.Entity<EmployeePerformanceReview>(entity =>
        {
            entity.Property(e => e.CashDiscrepancy).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.Comment).HasMaxLength(1000);
            entity.Property(e => e.CreatedAt)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.ReviewedBy).HasMaxLength(450);
            entity.Property(e => e.Score).HasColumnType("decimal(5, 2)");
        });

        modelBuilder.Entity<Gate>(entity =>
        {
            entity.Property(e => e.Code)
                .IsRequired()
                .HasMaxLength(30);
            entity.Property(e => e.Direction)
                .IsRequired()
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasDefaultValue("Both");
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.Name)
                .IsRequired()
                .HasMaxLength(200);

            entity.HasOne(d => d.ParkingLot).WithMany(p => p.Gates)
                .HasForeignKey(d => d.ParkingLotId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_Gates_ParkingLots");
        });

        modelBuilder.Entity<Holiday>(entity =>
        {
            entity.Property(e => e.Name)
                .IsRequired()
                .HasMaxLength(200);
        });

        modelBuilder.Entity<Integration>(entity =>
        {
            entity.Property(e => e.BaseUrl).HasMaxLength(500);
            entity.Property(e => e.Code)
                .IsRequired()
                .HasMaxLength(50);
            entity.Property(e => e.CreatedAt)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.IntegrationType)
                .IsRequired()
                .HasMaxLength(30)
                .IsUnicode(false);
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.Name)
                .IsRequired()
                .HasMaxLength(200);
            entity.Property(e => e.Provider)
                .IsRequired()
                .HasMaxLength(50);
            entity.Property(e => e.UpdatedAt).HasPrecision(3);
        });

        modelBuilder.Entity<Invoice>(entity =>
        {
            entity.Property(e => e.CreatedAt)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.DiscountAmount).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.DueDate).HasPrecision(0);
            entity.Property(e => e.InvoiceNumber)
                .IsRequired()
                .HasMaxLength(50);
            entity.Property(e => e.PaidAt).HasPrecision(3);
            entity.Property(e => e.Status)
                .IsRequired()
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasDefaultValue("Unpaid");
            entity.Property(e => e.SubTotal).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.TaxAmount).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.TotalAmount).HasColumnType("decimal(18, 2)");

            entity.HasOne(d => d.Session).WithMany(p => p.Invoices)
                .HasForeignKey(d => d.SessionId)
                .HasConstraintName("FK_Invoices_ParkingSessions");
        });

        modelBuilder.Entity<LinkedPaymentAccount>(entity =>
        {
            entity.Property(e => e.CreatedAt)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.MaskedInfo).HasMaxLength(100);
            entity.Property(e => e.Provider)
                .IsRequired()
                .HasMaxLength(30)
                .IsUnicode(false);
            entity.Property(e => e.ProviderTokenRef)
                .IsRequired()
                .HasMaxLength(200);
            entity.Property(e => e.RevokedAt).HasPrecision(3);
            entity.Property(e => e.Status)
                .IsRequired()
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasDefaultValue("Active");
        });

        modelBuilder.Entity<LotReview>(entity =>
        {
            entity.Property(e => e.Comment).HasMaxLength(1000);
            entity.Property(e => e.CreatedAt)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.IsVisible).HasDefaultValue(true);
        });

        modelBuilder.Entity<LoyaltyTransaction>(entity =>
        {
            entity.Property(e => e.CreatedAt)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.Description).HasMaxLength(300);
            entity.Property(e => e.ExpiresAt).HasPrecision(0);
            entity.Property(e => e.ReferenceId).HasMaxLength(50);
            entity.Property(e => e.ReferenceType)
                .HasMaxLength(30)
                .IsUnicode(false);
            entity.Property(e => e.TxnType)
                .IsRequired()
                .HasMaxLength(20)
                .IsUnicode(false);
        });

        modelBuilder.Entity<OtpCode>(entity =>
        {
            entity.Property(e => e.Channel)
                .IsRequired()
                .HasMaxLength(10)
                .IsUnicode(false);
            entity.Property(e => e.CodeHash)
                .IsRequired()
                .HasMaxLength(200);
            entity.Property(e => e.ConsumedAt).HasPrecision(3);
            entity.Property(e => e.CreatedAt)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.Destination)
                .IsRequired()
                .HasMaxLength(256);
            entity.Property(e => e.ExpiresAt).HasPrecision(3);
            entity.Property(e => e.Purpose)
                .IsRequired()
                .HasMaxLength(30)
                .IsUnicode(false);
            entity.Property(e => e.UserId).HasMaxLength(450);
        });

        modelBuilder.Entity<ParkingLot>(entity =>
        {
            entity.Property(e => e.Address)
                .IsRequired()
                .HasMaxLength(500);
            entity.Property(e => e.City).HasMaxLength(100);
            entity.Property(e => e.CloseTime).HasPrecision(0);
            entity.Property(e => e.ClosedFrom).HasPrecision(0);
            entity.Property(e => e.ClosedUntil).HasPrecision(0);
            entity.Property(e => e.ClosureReason).HasMaxLength(500);
            entity.Property(e => e.Code)
                .IsRequired()
                .HasMaxLength(30);
            entity.Property(e => e.CreatedAt)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.CreatedBy).HasMaxLength(450);
            entity.Property(e => e.Description).HasMaxLength(1000);
            entity.Property(e => e.District).HasMaxLength(100);
            entity.Property(e => e.ImageUrl).HasMaxLength(500);
            entity.Property(e => e.Latitude).HasColumnType("decimal(9, 6)");
            entity.Property(e => e.Longitude).HasColumnType("decimal(9, 6)");
            entity.Property(e => e.MaintenanceNotice).HasMaxLength(1000);
            entity.Property(e => e.ManagerUserId).HasMaxLength(450);
            entity.Property(e => e.Name)
                .IsRequired()
                .HasMaxLength(200);
            entity.Property(e => e.OpenTime).HasPrecision(0);
            entity.Property(e => e.Phone).HasMaxLength(30);
            entity.Property(e => e.Status)
                .IsRequired()
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasDefaultValue("Active");
            entity.Property(e => e.TimeZoneId)
                .IsRequired()
                .HasMaxLength(60)
                .IsUnicode(false)
                .HasDefaultValue("SE Asia Standard Time");
            entity.Property(e => e.UpdatedAt).HasPrecision(3);

            entity.HasOne(d => d.Partner).WithMany(p => p.ParkingLots)
                .HasForeignKey(d => d.PartnerId)
                .HasConstraintName("FK_ParkingLots_Partners");

            entity.HasIndex(e => e.Code).IsUnique();
        });

        modelBuilder.Entity<ParkingLotImage>(entity =>
        {
            entity.Property(e => e.Caption).HasMaxLength(200);
            entity.Property(e => e.ImageUrl)
                .IsRequired()
                .HasMaxLength(500);
        });

        modelBuilder.Entity<ParkingSession>(entity =>
        {
            entity.HasIndex(e => new { e.EntryPlateNormalized, e.Status }, "IX_ParkingSessions_EntryPlateNormalized_Status");

            entity.HasIndex(e => new { e.ParkingLotId, e.Status }, "IX_ParkingSessions_ParkingLotId_Status");

            entity.Property(e => e.DiscountAmount).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.EntryAnprConfidence).HasColumnType("decimal(5, 2)");
            entity.Property(e => e.EntryImageFrontUrl).HasMaxLength(500);
            entity.Property(e => e.EntryImageOverviewUrl).HasMaxLength(500);
            entity.Property(e => e.EntryImageRearUrl).HasMaxLength(500);
            entity.Property(e => e.EntryOperatorUserId).HasMaxLength(450);
            entity.Property(e => e.EntryPlateNormalized)
                .IsRequired()
                .HasMaxLength(20);
            entity.Property(e => e.EntryPlateNumber)
                .IsRequired()
                .HasMaxLength(20);
            entity.Property(e => e.EntryTime).HasPrecision(0);
            entity.Property(e => e.ExitAnprConfidence).HasColumnType("decimal(5, 2)");
            entity.Property(e => e.ExitImageFrontUrl).HasMaxLength(500);
            entity.Property(e => e.ExitImageOverviewUrl).HasMaxLength(500);
            entity.Property(e => e.ExitImageRearUrl).HasMaxLength(500);
            entity.Property(e => e.ExitOperatorUserId).HasMaxLength(450);
            entity.Property(e => e.ExitPlateNormalized).HasMaxLength(20);
            entity.Property(e => e.ExitPlateNumber).HasMaxLength(20);
            entity.Property(e => e.ExitTime).HasPrecision(0);
            entity.Property(e => e.FinalFee).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.OriginalFee).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.RowVersion)
                .IsRequired()
                .IsRowVersion()
                .IsConcurrencyToken();
            entity.Property(e => e.SessionCode)
                .IsRequired()
                .HasMaxLength(40);
            entity.Property(e => e.Status)
                .IsRequired()
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasDefaultValue("Ongoing");

            entity.HasOne(d => d.Customer).WithMany(p => p.ParkingSessions)
                .HasForeignKey(d => d.CustomerId)
                .HasConstraintName("FK_ParkingSessions_Customers");

            entity.HasOne(d => d.EntryGate).WithMany(p => p.ParkingSessionEntryGates)
                .HasForeignKey(d => d.EntryGateId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_ParkingSessions_EntryGates");

            entity.HasOne(d => d.ExitGate).WithMany(p => p.ParkingSessionExitGates)
                .HasForeignKey(d => d.ExitGateId)
                .HasConstraintName("FK_ParkingSessions_ExitGates");

            entity.HasOne(d => d.ParkingLot).WithMany(p => p.ParkingSessions)
                .HasForeignKey(d => d.ParkingLotId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_ParkingSessions_ParkingLots");

            entity.HasOne(d => d.Vehicle).WithMany(p => p.ParkingSessions)
                .HasForeignKey(d => d.VehicleId)
                .HasConstraintName("FK_ParkingSessions_Vehicles");
        });

        modelBuilder.Entity<Partner>(entity =>
        {
            entity.Property(e => e.Address).HasMaxLength(500);
            entity.Property(e => e.Code)
                .IsRequired()
                .HasMaxLength(50);
            entity.Property(e => e.ContactName).HasMaxLength(200);
            entity.Property(e => e.CreatedAt)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.Email).HasMaxLength(256);
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.Name)
                .IsRequired()
                .HasMaxLength(200);
            entity.Property(e => e.PartnerType)
                .IsRequired()
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasDefaultValue("Other");
            entity.Property(e => e.Phone).HasMaxLength(30);
            entity.Property(e => e.RevenueSharePercent).HasColumnType("decimal(5, 2)");
            entity.Property(e => e.TaxCode).HasMaxLength(30);
            entity.Property(e => e.UpdatedAt).HasPrecision(3);
        });

        modelBuilder.Entity<Payment>(entity =>
        {
            entity.Property(e => e.Amount).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.CreatedAt)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.Currency)
                .IsRequired()
                .HasMaxLength(3)
                .IsUnicode(false)
                .HasDefaultValue("VND")
                .IsFixedLength();
            entity.Property(e => e.GatewayProvider)
                .HasMaxLength(30)
                .IsUnicode(false);
            entity.Property(e => e.PaidAt).HasPrecision(3);
            entity.Property(e => e.PaymentCode)
                .IsRequired()
                .HasMaxLength(50);
            entity.Property(e => e.PaymentMethod)
                .IsRequired()
                .HasMaxLength(20)
                .IsUnicode(false);
            entity.Property(e => e.Status)
                .IsRequired()
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasDefaultValue("Pending");
            entity.Property(e => e.TransactionRef).HasMaxLength(100);

            entity.HasOne(d => d.Invoice).WithMany(p => p.Payments)
                .HasForeignKey(d => d.InvoiceId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_Payments_Invoices");
        });

        modelBuilder.Entity<Permission>(entity =>
        {
            entity.Property(e => e.Code)
                .IsRequired()
                .HasMaxLength(100);
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.Property(e => e.Module)
                .IsRequired()
                .HasMaxLength(50);
            entity.Property(e => e.Name)
                .IsRequired()
                .HasMaxLength(200);
        });

        modelBuilder.Entity<PricingRule>(entity =>
        {
            entity.Property(e => e.ApprovedAt).HasPrecision(0);
            entity.Property(e => e.ApprovedBy).HasMaxLength(450);
            entity.Property(e => e.BlockPrice).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.ChargeModel)
                .IsRequired()
                .HasMaxLength(20)
                .IsUnicode(false);
            entity.Property(e => e.Code)
                .IsRequired()
                .HasMaxLength(40);
            entity.Property(e => e.CreatedAt)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.CreatedBy).HasMaxLength(450);
            entity.Property(e => e.Currency)
                .IsRequired()
                .HasMaxLength(3)
                .IsUnicode(false)
                .HasDefaultValue("VND")
                .IsFixedLength();
            entity.Property(e => e.DayPrice).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.DayStartTime).HasPrecision(0);
            entity.Property(e => e.EffectiveFrom).HasPrecision(0);
            entity.Property(e => e.EffectiveTo).HasPrecision(0);
            entity.Property(e => e.EntryFee).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.FirstBlockPrice).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.LostTicketFee).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.MaxDailyFee).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.Name)
                .IsRequired()
                .HasMaxLength(200);
            entity.Property(e => e.NightPrice).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.NightStartTime).HasPrecision(0);
            entity.Property(e => e.NoShowFee).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.OvernightSurcharge).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.OverstaySurchargePerDay).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.Status)
                .IsRequired()
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasDefaultValue("Draft");
            entity.Property(e => e.UpdatedAt).HasPrecision(3);
            entity.Property(e => e.VersionNo).HasDefaultValue(1);
        });

        modelBuilder.Entity<PricingRuleHistory>(entity =>
        {
            entity.Property(e => e.ChangeType)
                .IsRequired()
                .HasMaxLength(20)
                .IsUnicode(false);
            entity.Property(e => e.ChangedAt)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.ChangedBy).HasMaxLength(450);
            entity.Property(e => e.Reason).HasMaxLength(500);
        });

        modelBuilder.Entity<PricingTimeBand>(entity =>
        {
            entity.Property(e => e.AdjustmentType)
                .IsRequired()
                .HasMaxLength(15)
                .IsUnicode(false)
                .HasDefaultValue("Multiplier");
            entity.Property(e => e.AdjustmentValue).HasColumnType("decimal(10, 4)");
            entity.Property(e => e.DayType)
                .IsRequired()
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasDefaultValue("All");
            entity.Property(e => e.EndTime).HasPrecision(0);
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.Name)
                .IsRequired()
                .HasMaxLength(100);
            entity.Property(e => e.StartTime).HasPrecision(0);
        });

        modelBuilder.Entity<Promotion>(entity =>
        {
            entity.Property(e => e.Code)
                .IsRequired()
                .HasMaxLength(40);
            entity.Property(e => e.CreatedAt)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.CreatedBy).HasMaxLength(450);
            entity.Property(e => e.Description).HasMaxLength(1000);
            entity.Property(e => e.DiscountValue).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.MaxDiscountAmount).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.MinFeeAmount).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.Name)
                .IsRequired()
                .HasMaxLength(200);
            entity.Property(e => e.PromotionType)
                .IsRequired()
                .HasMaxLength(20)
                .IsUnicode(false);
            entity.Property(e => e.Status)
                .IsRequired()
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasDefaultValue("Draft");
            entity.Property(e => e.UpdatedAt).HasPrecision(3);
            entity.Property(e => e.ValidFrom).HasPrecision(0);
            entity.Property(e => e.ValidTo).HasPrecision(0);
        });

        modelBuilder.Entity<RefreshToken>(entity =>
        {
            entity.Property(e => e.CreatedAt)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.DeviceInfo).HasMaxLength(300);
            entity.Property(e => e.ExpiresAt).HasPrecision(3);
            entity.Property(e => e.IpAddress)
                .HasMaxLength(45)
                .IsUnicode(false);
            entity.Property(e => e.ReplacedByTokenHash).HasMaxLength(200);
            entity.Property(e => e.RevokedAt).HasPrecision(3);
            entity.Property(e => e.RevokedReason).HasMaxLength(200);
            entity.Property(e => e.TokenHash)
                .IsRequired()
                .HasMaxLength(200);
            entity.Property(e => e.UserId)
                .IsRequired()
                .HasMaxLength(450);
        });

        modelBuilder.Entity<Reservation>(entity =>
        {
            entity.Property(e => e.CancelledAt).HasPrecision(3);
            entity.Property(e => e.CreatedAt)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.DepositAmount).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.EndTime).HasPrecision(0);
            entity.Property(e => e.ReservationCode)
                .IsRequired()
                .HasMaxLength(40);
            entity.Property(e => e.StartTime).HasPrecision(0);
            entity.Property(e => e.Status)
                .IsRequired()
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasDefaultValue("Pending");
        });

        modelBuilder.Entity<RolePermission>(entity =>
        {
            entity.HasKey(e => new { e.RoleId, e.PermissionId });

            entity.Property(e => e.GrantedAt)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
        });

        modelBuilder.Entity<SessionViolation>(entity =>
        {
            entity.Property(e => e.FineAmount).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.Note).HasMaxLength(500);
            entity.Property(e => e.ReportedAt)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.ReportedBy).HasMaxLength(450);
            entity.Property(e => e.ViolationType)
                .IsRequired()
                .HasMaxLength(30)
                .IsUnicode(false);
            entity.Property(e => e.Status)
                .HasMaxLength(20)
                .IsUnicode(false);
            entity.Property(e => e.ApprovalMode)
                .HasMaxLength(20)
                .IsUnicode(false);
            entity.Property(e => e.DecidedBy).HasMaxLength(450);
            entity.Property(e => e.DecidedAt).HasPrecision(3);
            entity.Property(e => e.DecisionNote).HasMaxLength(500);
        });

        modelBuilder.Entity<Shift>(entity =>
        {
            entity.Property(e => e.ApprovedAt).HasPrecision(0);
            entity.Property(e => e.ApprovedBy).HasMaxLength(450);
            entity.Property(e => e.CashDifference).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.CountedCash).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.EndedAt).HasPrecision(0);
            entity.Property(e => e.ExpectedCash).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.HandoverNote).HasMaxLength(1000);
            entity.Property(e => e.Note).HasMaxLength(1000);
            entity.Property(e => e.OpeningCash).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.RowVersion)
                .IsRequired()
                .IsRowVersion()
                .IsConcurrencyToken();
            entity.Property(e => e.ShiftCode)
                .IsRequired()
                .HasMaxLength(40);
            entity.Property(e => e.StartedAt).HasPrecision(0);
            entity.Property(e => e.Status)
                .IsRequired()
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasDefaultValue("Open");
        });

        modelBuilder.Entity<ShiftCashCount>(entity =>
        {
            entity.Property(e => e.Denomination).HasColumnType("decimal(18, 0)");
        });

        modelBuilder.Entity<ShiftSchedule>(entity =>
        {
            entity.Property(e => e.CheckInAt).HasPrecision(0);
            entity.Property(e => e.CheckOutAt).HasPrecision(0);
            entity.Property(e => e.CreatedAt)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.CreatedBy).HasMaxLength(450);
            entity.Property(e => e.Note).HasMaxLength(300);
            entity.Property(e => e.Status)
                .IsRequired()
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasDefaultValue("Scheduled");
        });

        modelBuilder.Entity<ShiftTemplate>(entity =>
        {
            entity.Property(e => e.EndTime).HasPrecision(0);
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.Name)
                .IsRequired()
                .HasMaxLength(100);
            entity.Property(e => e.StartTime).HasPrecision(0);
        });

        modelBuilder.Entity<Slot>(entity =>
        {
            entity.Property(e => e.Code)
                .IsRequired()
                .HasMaxLength(30);
            entity.Property(e => e.DedicatedNote).HasMaxLength(200);
            entity.Property(e => e.Height).HasColumnType("decimal(8, 2)");
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.PosX).HasColumnType("decimal(8, 2)");
            entity.Property(e => e.PosY).HasColumnType("decimal(8, 2)");
            entity.Property(e => e.RowVersion)
                .IsRequired()
                .IsRowVersion()
                .IsConcurrencyToken();
            entity.Property(e => e.Status)
                .IsRequired()
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasDefaultValue("Available");
            entity.Property(e => e.Width).HasColumnType("decimal(8, 2)");

            entity.HasOne(d => d.VehicleType).WithMany(p => p.Slots)
                .HasForeignKey(d => d.VehicleTypeId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_Slots_VehicleTypes");

            entity.HasOne(d => d.Zone).WithMany(p => p.Slots)
                .HasForeignKey(d => d.ZoneId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_Slots_Zones");

            entity.HasIndex(e => new { e.ZoneId, e.Code }).IsUnique();
        });

        modelBuilder.Entity<Subscription>(entity =>
        {
            entity.Property(e => e.CancelReason).HasMaxLength(500);
            entity.Property(e => e.CancelledAt).HasPrecision(3);
            entity.Property(e => e.CreatedAt)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.Status)
                .IsRequired()
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasDefaultValue("Active");
            entity.Property(e => e.SubscriptionCode)
                .IsRequired()
                .HasMaxLength(40);
            entity.Property(e => e.UpdatedAt).HasPrecision(3);
        });

        modelBuilder.Entity<SubscriptionPlan>(entity =>
        {
            entity.Property(e => e.Code)
                .IsRequired()
                .HasMaxLength(40);
            entity.Property(e => e.CreatedAt)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.Property(e => e.DurationDays).HasDefaultValue(30);
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.Name)
                .IsRequired()
                .HasMaxLength(200);
            entity.Property(e => e.Price).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.UpdatedAt).HasPrecision(3);
        });

        modelBuilder.Entity<SubscriptionRenewal>(entity =>
        {
            entity.Property(e => e.AmountPaid).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.RenewedAt)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.RenewedBy).HasMaxLength(450);
        });

        modelBuilder.Entity<CustomerAvatar>(entity =>
        {
            entity.ToTable("CustomerAvatars");
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.HasIndex(e => e.CustomerId, "UX_CustomerAvatars_CustomerId").IsUnique();
            entity.Property(e => e.Content).IsRequired();
            entity.Property(e => e.ContentType)
                .IsRequired()
                .HasMaxLength(50)
                .IsUnicode(false);
            entity.Property(e => e.CreatedAt).HasPrecision(3);
        });

        modelBuilder.Entity<SystemSetting>(entity =>
        {
            entity.Property(e => e.Category).HasMaxLength(50);
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.Property(e => e.SettingKey)
                .IsRequired()
                .HasMaxLength(100);
            entity.Property(e => e.UpdatedAt).HasPrecision(3);
            entity.Property(e => e.UpdatedBy).HasMaxLength(450);
            entity.Property(e => e.ValueType)
                .IsRequired()
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasDefaultValue("string");
        });

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasIndex(e => e.NormalizedEmail, "EmailIndex");

            entity.HasIndex(e => e.NormalizedUserName, "UserNameIndex")
                .IsUnique()
                .HasFilter("([NormalizedUserName] IS NOT NULL)");

            entity.Property(e => e.Email).HasMaxLength(256);
            entity.Property(e => e.NormalizedEmail).HasMaxLength(256);
            entity.Property(e => e.NormalizedUserName).HasMaxLength(256);
            entity.Property(e => e.Role)
                .IsRequired()
                .HasMaxLength(50)
                .HasDefaultValue("User");
            entity.Property(e => e.UserName).HasMaxLength(256);

            entity.HasMany(d => d.Roles).WithMany(p => p.Users)
                .UsingEntity<Dictionary<string, object>>(
                    "AspNetUserRole",
                    r => r.HasOne<AspNetRole>().WithMany().HasForeignKey("RoleId"),
                    l => l.HasOne<User>().WithMany().HasForeignKey("UserId"),
                    j =>
                    {
                        j.HasKey("UserId", "RoleId");
                        j.ToTable("AspNetUserRoles");
                        j.HasIndex(new[] { "RoleId" }, "IX_AspNetUserRoles_RoleId");
                    });
        });

        modelBuilder.Entity<UserConsent>(entity =>
        {
            entity.Property(e => e.ConsentType)
                .IsRequired()
                .HasMaxLength(50)
                .IsUnicode(false);
            entity.Property(e => e.GrantedAt)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.IpAddress)
                .HasMaxLength(45)
                .IsUnicode(false);
            entity.Property(e => e.RevokedAt).HasPrecision(3);
            entity.Property(e => e.UserId)
                .IsRequired()
                .HasMaxLength(450);
            entity.Property(e => e.Version)
                .HasMaxLength(20)
                .IsUnicode(false);
        });

        modelBuilder.Entity<UserLotAccess>(entity =>
        {
            entity.ToTable("UserLotAccess");

            entity.Property(e => e.ExpiresAt).HasPrecision(3);
            entity.Property(e => e.GrantedAt)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.GrantedBy).HasMaxLength(450);
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.RoleId).HasMaxLength(450);
            entity.Property(e => e.UserId)
                .IsRequired()
                .HasMaxLength(450);
        });

        modelBuilder.Entity<Vehicle>(entity =>
        {
            entity.Property(e => e.Id).ValueGeneratedNever();

            entity.HasIndex(e => e.PlateNormalized, "IX_Vehicles_PlateNormalized");

            entity.Property(e => e.Brand).HasMaxLength(100);
            entity.Property(e => e.Color).HasMaxLength(50);
            entity.Property(e => e.CitizenId).HasMaxLength(12).IsUnicode(false);
            entity.Property(e => e.IsDefault).HasDefaultValue(false);
            entity.Property(e => e.CreatedAt)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.Model).HasMaxLength(100);
            entity.Property(e => e.PlateNormalized)
                .IsRequired()
                .HasMaxLength(20);
            entity.Property(e => e.PlateNumber)
                .IsRequired()
                .HasMaxLength(20);
            entity.Property(e => e.UpdatedAt).HasPrecision(3);

            entity.HasOne(d => d.Customer).WithMany(p => p.Vehicles)
                .HasForeignKey(d => d.CustomerId)
                .HasConstraintName("FK_Vehicles_Customers");

            entity.HasOne(d => d.VehicleType).WithMany(p => p.Vehicles)
                .HasForeignKey(d => d.VehicleTypeId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_Vehicles_VehicleTypes");
        });

        modelBuilder.Entity<VehicleType>(entity =>
        {
            entity.Property(e => e.Code)
                .IsRequired()
                .HasMaxLength(30)
                .IsUnicode(false);
            entity.Property(e => e.Description).HasMaxLength(300);
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.Name)
                .IsRequired()
                .HasMaxLength(100);
        });

        modelBuilder.Entity<Voucher>(entity =>
        {
            entity.HasIndex(e => e.VoucherCode, "UQ_Vouchers_Code").IsUnique();

            entity.Property(e => e.IssuedAt)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.Status)
                .IsRequired()
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasDefaultValue("Issued");
            entity.Property(e => e.UsedAt).HasPrecision(3);
            entity.Property(e => e.VoucherCode)
                .IsRequired()
                .HasMaxLength(50);
        });

        modelBuilder.Entity<Wallet>(entity =>
        {
            entity.Property(e => e.Balance).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.Currency)
                .IsRequired()
                .HasMaxLength(3)
                .IsUnicode(false)
                .HasDefaultValue("VND")
                .IsFixedLength();
            entity.Property(e => e.RowVersion)
                .IsRequired()
                .IsRowVersion()
                .IsConcurrencyToken();
            entity.Property(e => e.Status)
                .IsRequired()
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasDefaultValue("Active");
            entity.Property(e => e.UpdatedAt)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
        });

        modelBuilder.Entity<WalletTransaction>(entity =>
        {
            entity.Property(e => e.Amount).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.BalanceAfter).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.BalanceBefore).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.CreatedAt)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.CreatedBy).HasMaxLength(450);
            entity.Property(e => e.Description).HasMaxLength(300);
            entity.Property(e => e.ReferenceId).HasMaxLength(50);
            entity.Property(e => e.ReferenceType)
                .HasMaxLength(30)
                .IsUnicode(false);
            entity.Property(e => e.TxnType)
                .IsRequired()
                .HasMaxLength(20)
                .IsUnicode(false);
        });

        modelBuilder.Entity<WebhookEndpoint>(entity =>
        {
            entity.Property(e => e.CreatedAt)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.EventTypes).HasMaxLength(500);
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.LastDeliveryAt).HasPrecision(3);
            entity.Property(e => e.SecretHash).HasMaxLength(200);
            entity.Property(e => e.Url)
                .IsRequired()
                .HasMaxLength(500);
        });

        modelBuilder.Entity<Zone>(entity =>
        {
            entity.Property(e => e.Code)
                .IsRequired()
                .HasMaxLength(30);
            entity.Property(e => e.CreatedAt)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.Name)
                .IsRequired()
                .HasMaxLength(200);
            entity.Property(e => e.UpdatedAt).HasPrecision(3);
            entity.Property(e => e.ZoneType)
                .IsRequired()
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasDefaultValue("Area");

            entity.HasOne(d => d.ParkingLot).WithMany(p => p.Zones)
                .HasForeignKey(d => d.ParkingLotId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_Zones_ParkingLots");

            entity.HasIndex(e => new { e.ParkingLotId, e.Code }).IsUnique();

            entity.HasOne(d => d.ZoneCounter).WithOne(p => p.Zone)
                .HasForeignKey<ZoneCounter>(d => d.ZoneId)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("FK_ZoneCounters_Zones");
        });

        modelBuilder.Entity<ZoneCounter>(entity =>
        {
            entity.ToTable("ZoneCounters");

            entity.Property(e => e.LastUpdatedAt)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");

            entity.HasIndex(e => e.ZoneId).IsUnique();
        });

        modelBuilder.Entity<ZoneOccupancy>(entity =>
        {
            entity.ToTable("ZoneOccupancy");

            entity.Property(e => e.RowVersion)
                .IsRequired()
                .IsRowVersion()
                .IsConcurrencyToken();
            entity.Property(e => e.UpdatedAt)
                .HasPrecision(3)
                .HasDefaultValueSql("(sysutcdatetime())");
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
