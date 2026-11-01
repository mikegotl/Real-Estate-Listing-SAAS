using ListingStudio.Domain.Campaigns;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ListingStudio.Infrastructure.Persistence.Configurations;

public sealed class CampaignVideoLibraryConfiguration : IEntityTypeConfiguration<CampaignVideoLibrary>
{
    public void Configure(EntityTypeBuilder<CampaignVideoLibrary> builder)
    {
        builder.ToTable("CampaignVideoLibraries");
        builder.HasKey(library => library.Id);
        // Identities are assigned in the domain, so a video added to a loaded library is inserted, not updated.
        builder.Property(library => library.Id).ValueGeneratedNever();
        builder.Property(library => library.Selection).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(library => library.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(library => library.RuleVersion).HasMaxLength(CampaignVideoLibrary.MaximumRuleVersionLength);
        builder.Property(library => library.RecommendationSnapshotJson).HasColumnType("jsonb");
        builder.HasIndex(library => library.CampaignGenerationJobId).IsUnique();
        builder.HasIndex(library => new { library.OrganizationId, library.PropertyId, library.CreatedAtUtc });
        builder.HasIndex(library => new { library.Status, library.UpdatedAtUtc });
        builder.HasOne<CampaignGenerationJob>()
            .WithMany()
            .HasForeignKey(library => new
            {
                library.CampaignGenerationJobId,
                library.PropertyId,
                library.OrganizationId,
            })
            .HasPrincipalKey(job => new { job.Id, job.PropertyId, job.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(library => library.Variants)
            .WithOne(variant => variant.Library)
            .HasForeignKey(variant => variant.CampaignVideoLibraryId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class CampaignVideoVariantConfiguration : IEntityTypeConfiguration<CampaignVideoVariant>
{
    public void Configure(EntityTypeBuilder<CampaignVideoVariant> builder)
    {
        builder.ToTable("CampaignVideoVariants");
        builder.HasKey(variant => variant.Id);
        builder.Property(variant => variant.Id).ValueGeneratedNever();
        builder.Property(variant => variant.Format).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(variant => variant.Purpose).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(variant => variant.VoiceoverMode).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(variant => variant.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(variant => variant.HookText).HasMaxLength(CampaignVideoVariant.MaximumHookLength);
        builder.Property(variant => variant.HookGroundingKey).HasMaxLength(CampaignVideoVariant.MaximumGroundingKeyLength);
        builder.Property(variant => variant.CallToAction).HasMaxLength(CampaignVideoVariant.MaximumCallToActionLength);
        builder.Property(variant => variant.RecipeJson).HasColumnType("jsonb").IsRequired();
        builder.Property(variant => variant.RecipeVersion)
            .HasMaxLength(CampaignVideoVariant.MaximumRecipeVersionLength).IsRequired();
        builder.Property(variant => variant.CaptionsJson).HasColumnType("jsonb");
        builder.Property(variant => variant.Error).HasMaxLength(500);
        builder.Property(variant => variant.IdempotencyKey).HasMaxLength(CampaignVideoVariant.MaximumIdempotencyKeyLength);
        builder.Property(variant => variant.HookVariant).HasMaxLength(8).HasDefaultValue(CampaignVideoVariant.DefaultHookVariant).IsRequired();
        builder.Property(variant => variant.NarrationLocalizationJson).HasColumnType("text");
        builder.HasIndex(variant => new { variant.CampaignVideoLibraryId, variant.IdempotencyKey })
            .IsUnique()
            .HasFilter("\"IdempotencyKey\" IS NOT NULL");
        builder.HasIndex(variant => new { variant.CampaignVideoLibraryId, variant.Format, variant.Sequence }).IsUnique();
        builder.HasIndex(variant => new { variant.OrganizationId, variant.CampaignVideoLibraryId });
        builder.HasIndex(variant => new { variant.OrganizationId, variant.Format, variant.Status });
        builder.HasOne(variant => variant.Deliverable)
            .WithOne()
            .HasForeignKey<CampaignDeliverable>(deliverable => deliverable.CampaignVideoVariantId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
