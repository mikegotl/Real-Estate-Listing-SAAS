using ListingStudio.Domain.Campaigns;
using ListingStudio.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ListingStudio.Infrastructure.Persistence.Configurations;

public sealed class CampaignSettingsPresetConfiguration : IEntityTypeConfiguration<CampaignSettingsPreset>
{
    public void Configure(EntityTypeBuilder<CampaignSettingsPreset> builder)
    {
        builder.ToTable("CampaignSettingsPresets");
        builder.HasKey(preset => preset.Id);
        builder.Property(preset => preset.OwnerUserId).HasMaxLength(450).IsRequired();
        builder.Property(preset => preset.Name).HasMaxLength(CampaignSettingsPreset.MaximumNameLength).IsRequired();
        builder.Property(preset => preset.NormalizedName)
            .HasMaxLength(CampaignSettingsPreset.MaximumNameLength)
            .IsRequired();
        builder.Property(preset => preset.SettingsJson)
            .HasMaxLength(CampaignPresetSettings.MaximumJsonLength)
            .IsRequired();
        builder.HasIndex(preset => new { preset.OrganizationId, preset.OwnerUserId, preset.NormalizedName }).IsUnique();
        builder.HasIndex(preset => new { preset.OrganizationId, preset.OwnerUserId })
            .IsUnique()
            .HasFilter("\"IsDefault\"")
            .HasDatabaseName("IX_CampaignSettingsPresets_OneDefaultPerOwner");
        builder.HasIndex(preset => new { preset.OrganizationId, preset.IsShared });

        builder.HasOne(preset => preset.Organization)
            .WithMany()
            .HasForeignKey(preset => preset.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(preset => preset.OwnerUserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
