using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PMS.DataAccess.Identity;

namespace PMS.DataAccess.Persistence.Configurations;

public class ApplicationUserConfiguration : IEntityTypeConfiguration<ApplicationUser>
{
    public void Configure(EntityTypeBuilder<ApplicationUser> builder)
    {
        builder.ToTable("Users");

        builder.Property(u => u.Id)
            .HasColumnName("UserId");

        builder.Property(u => u.PasswordHash)
            .HasColumnName("Password");

        builder.Property(u => u.Role)
            .HasMaxLength(50)
            .HasDefaultValue("User")
            .IsRequired();
    }
}
