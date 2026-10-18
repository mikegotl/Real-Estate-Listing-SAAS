using ListingStudio.Domain.Notifications;
using ListingStudio.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ListingStudio.Infrastructure.Persistence.Configurations;

public sealed class UserNotificationConfiguration : IEntityTypeConfiguration<UserNotification>
{
    public void Configure(EntityTypeBuilder<UserNotification> builder)
    {
        builder.ToTable("UserNotifications");
        builder.HasKey(notification => notification.Id);
        builder.Property(notification => notification.UserId).HasMaxLength(450).IsRequired();
        builder.Property(notification => notification.Kind).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(notification => notification.Outcome).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(notification => notification.Title).HasMaxLength(UserNotification.MaximumTitleLength).IsRequired();
        builder.Property(notification => notification.Message).HasMaxLength(UserNotification.MaximumMessageLength).IsRequired();
        builder.Property(notification => notification.Tab).HasMaxLength(UserNotification.MaximumTabLength).IsRequired();
        builder.Ignore(notification => notification.IsRead);
        builder.HasIndex(notification => new { notification.UserId, notification.CreatedAtUtc });

        builder.HasOne(notification => notification.Organization)
            .WithMany()
            .HasForeignKey(notification => notification.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(notification => notification.Property)
            .WithMany()
            .HasForeignKey(notification => new { notification.PropertyId, notification.OrganizationId })
            .HasPrincipalKey(property => new { property.Id, property.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(notification => notification.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
