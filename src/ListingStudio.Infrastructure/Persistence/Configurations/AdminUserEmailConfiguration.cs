using ListingStudio.Domain.Administration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ListingStudio.Infrastructure.Persistence.Configurations;

public sealed class AdminUserEmailConfiguration : IEntityTypeConfiguration<AdminUserEmail>
{
    public void Configure(EntityTypeBuilder<AdminUserEmail> builder)
    {
        builder.ToTable("AdminUserEmails");
        builder.HasKey(email => email.Id);
        builder.Property(email => email.SentByUserId).HasMaxLength(450).IsRequired();
        builder.Property(email => email.RecipientUserId).HasMaxLength(450).IsRequired();
        builder.Property(email => email.RecipientEmail).HasMaxLength(AdminUserEmail.MaximumEmailLength).IsRequired();
        builder.Property(email => email.Subject).HasMaxLength(AdminUserEmail.MaximumSubjectLength).IsRequired();
        builder.Property(email => email.Body).HasMaxLength(AdminUserEmail.MaximumBodyLength).IsRequired();
        builder.Property(email => email.LastError).HasMaxLength(AdminUserEmail.MaximumErrorLength);
        builder.Ignore(email => email.IsPending);
        builder.HasIndex(email => email.QueuedAtUtc);
        builder.HasIndex(email => new { email.SentAtUtc, email.QueuedAtUtc });
        builder.HasIndex(email => email.BatchId);
        builder.HasIndex(email => email.RecipientUserId);

        // Keep the log when an account is deleted: no foreign keys to the users table.
    }
}
