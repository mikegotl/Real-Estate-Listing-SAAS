using ListingStudio.Domain.Administration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ListingStudio.Infrastructure.Persistence.Configurations;

public sealed class AiUsageEventConfiguration : IEntityTypeConfiguration<AiUsageEvent>
{
    public void Configure(EntityTypeBuilder<AiUsageEvent> builder)
    {
        builder.ToTable("AiUsageEvents");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Provider).HasMaxLength(50).IsRequired();
        builder.Property(item => item.Model).HasMaxLength(100).IsRequired();
        builder.Property(item => item.Operation).HasMaxLength(100).IsRequired();
        builder.Property(item => item.EstimatedCostUsd).HasPrecision(18, 8);
        builder.Property(item => item.FailureCategory).HasMaxLength(50);
        builder.Property(item => item.PropertyReference).HasMaxLength(64);
        builder.HasIndex(item => new { item.OccurredAtUtc, item.Provider, item.Operation });
        builder.HasIndex(item => new { item.OccurredAtUtc, item.PropertyReference });
    }
}
