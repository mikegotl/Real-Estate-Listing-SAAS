namespace ListingStudio.Infrastructure.Persistence.Configurations;

using ListingStudio.Domain.Billing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class OrganizationBillingAccountConfiguration : IEntityTypeConfiguration<OrganizationBillingAccount>
{
    public void Configure(EntityTypeBuilder<OrganizationBillingAccount> builder)
    {
        builder.ToTable("OrganizationBillingAccounts");
        builder.HasKey(account => account.OrganizationId);
        builder.Property(account => account.StripeCustomerId).HasMaxLength(255);
        builder.Property(account => account.StripeSubscriptionId).HasMaxLength(255);
        builder.Property(account => account.Plan).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(account => account.SubscriptionStatus).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.HasIndex(account => account.StripeCustomerId).IsUnique();
        builder.HasIndex(account => account.StripeSubscriptionId).IsUnique();
        builder.HasOne(account => account.Organization)
            .WithOne()
            .HasForeignKey<OrganizationBillingAccount>(account => account.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
