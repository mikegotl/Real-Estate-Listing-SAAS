namespace ListingStudio.Infrastructure.Persistence.Configurations;

using ListingStudio.Domain.Billing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class BillingWebhookReceiptConfiguration : IEntityTypeConfiguration<BillingWebhookReceipt>
{
    public void Configure(EntityTypeBuilder<BillingWebhookReceipt> builder)
    {
        builder.ToTable("BillingWebhookReceipts");
        builder.HasKey(receipt => receipt.EventId);
        builder.Property(receipt => receipt.EventId).HasMaxLength(255);
        builder.Property(receipt => receipt.EventType).HasMaxLength(100).IsRequired();
        builder.Property(receipt => receipt.PayloadSha256).HasMaxLength(64).IsFixedLength().IsRequired();
        builder.HasIndex(receipt => receipt.ProcessedAtUtc);
    }
}
