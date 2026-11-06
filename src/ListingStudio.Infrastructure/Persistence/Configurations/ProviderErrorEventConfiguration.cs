using ListingStudio.Domain.Administration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ListingStudio.Infrastructure.Persistence.Configurations;

public sealed class ProviderErrorEventConfiguration : IEntityTypeConfiguration<ProviderErrorEvent>
{
    public void Configure(EntityTypeBuilder<ProviderErrorEvent> builder)
    {
        builder.ToTable("ProviderErrorEvents");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Provider).HasMaxLength(100).IsRequired();
        builder.Property(item => item.Action).HasMaxLength(300).IsRequired();
        builder.Property(item => item.ResponseText).HasMaxLength(ProviderErrorEvent.MaximumResponseLength);
        builder.Property(item => item.ProviderRequestId).HasMaxLength(200);
        builder.Property(item => item.RequestBody).HasMaxLength(ProviderErrorEvent.MaximumRequestLength);
        builder.Property(item => item.Stage).HasMaxLength(100);
        builder.HasIndex(item => item.OccurredAtUtc);
        builder.HasIndex(item => item.CampaignGenerationJobId);
    }
}
