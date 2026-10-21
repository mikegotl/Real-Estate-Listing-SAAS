using ListingStudio.Domain.Campaigns;
using ListingStudio.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ListingStudio.Infrastructure.Persistence.Configurations;

public sealed class CampaignGenerationJobConfiguration : IEntityTypeConfiguration<CampaignGenerationJob>
{
    public void Configure(EntityTypeBuilder<CampaignGenerationJob> builder)
    {
        builder.ToTable("CampaignGenerationJobs");
        builder.HasKey(job => job.Id);
        builder.HasAlternateKey(job => new { job.Id, job.PropertyId, job.OrganizationId });
        builder.HasAlternateKey(job => new { job.Id, job.OrganizationId });
        builder.Property(job => job.RequestedByUserId).HasMaxLength(450).IsRequired();
        builder.Property(job => job.SourceFingerprint).HasMaxLength(64).IsRequired();
        builder.Property(job => job.RequestedOutputs)
            .HasConversion<string>()
            .HasMaxLength(128)
            .HasDefaultValue(CampaignOutputSelection.LegacyStandard)
            .HasSentinel(CampaignOutputSelection.None)
            .IsRequired();
        builder.Property(job => job.CreativeElements)
            .HasConversion<string>()
            .HasMaxLength(256)
            .HasDefaultValue(CampaignCreativeOptions.Legacy.Elements)
            .IsRequired();
        builder.Property(job => job.Soundtrack)
            .HasConversion<string>()
            .HasMaxLength(32)
            .HasDefaultValue(CampaignSoundtrack.None)
            .IsRequired();
        builder.Property(job => job.CaptionStyle)
            .HasConversion<string>()
            .HasMaxLength(32)
            .HasDefaultValue(CampaignCaptionStyle.Classic)
            .IsRequired();
        builder.Property(job => job.MarketingTemplate)
            .HasConversion<string>()
            .HasMaxLength(32)
            .HasDefaultValue(CampaignMarketingTemplate.BalancedShowcase)
            .HasSentinel(CampaignMarketingTemplate.BalancedShowcase)
            .IsRequired();
        builder.Property(job => job.PriceReductionAmount).HasPrecision(14, 2);
        builder.Property(job => job.PriceReductionVerified)
            .HasDefaultValue(false)
            .IsRequired();
        builder.Property(job => job.AgentAvatarConsentConfirmed)
            .HasDefaultValue(false)
            .IsRequired();
        builder.Property(job => job.NarrationRegion)
            .HasConversion<string>()
            .HasMaxLength(32)
            .HasDefaultValue(CampaignNarrationRegion.UnitedStates)
            .HasSentinel(CampaignNarrationRegion.UnitedStates)
            .IsRequired();
        builder.Property(job => job.SpokenLanguage)
            .HasConversion<string>()
            .HasMaxLength(32)
            .HasDefaultValue(CampaignLanguage.English)
            .HasSentinel(CampaignLanguage.English)
            .IsRequired();
        builder.Property(job => job.CaptionLanguage)
            .HasConversion<string>()
            .HasMaxLength(32)
            .HasDefaultValue(CampaignLanguage.English)
            .HasSentinel(CampaignLanguage.English)
            .IsRequired();
        builder.Property(job => job.VoiceAccent)
            .HasConversion<string>()
            .HasMaxLength(32)
            .HasDefaultValue(CampaignVoiceAccent.American)
            .HasSentinel(CampaignVoiceAccent.American)
            .IsRequired();
        builder.Property(job => job.LocalizeAgentCard)
            .HasDefaultValue(true)
            .HasSentinel(true)
            .IsRequired();
        builder.Property(job => job.LocalizedCopyJson).HasColumnType("text");
        builder.Property(job => job.LongFormLocalizedCopyJson).HasColumnType("text");
        builder.HasOne<AgentPortrait>()
            .WithMany()
            .HasForeignKey(job => new { job.AgentPortraitId, job.OrganizationId })
            .HasPrincipalKey(portrait => new { portrait.Id, portrait.OrganizationId })
            .OnDelete(DeleteBehavior.Restrict);
        builder.Property(job => job.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(job => job.CurrentStage).HasConversion<string>().HasMaxLength(64).IsRequired();
        builder.Property(job => job.LastError).HasMaxLength(1_000);
        builder.Property(job => job.SocialCaptionLong).HasMaxLength(4_000);
        builder.Property(job => job.SocialCaptionShort).HasMaxLength(1_000);
        builder.HasIndex(job => new { job.OrganizationId, job.PropertyId, job.CreatedAtUtc });
        builder.HasIndex(job => new { job.OrganizationId, job.PropertyId, job.SourceFingerprint });
        builder.HasIndex(job => new { job.Status, job.NextAttemptAtUtc, job.LeaseExpiresAtUtc });
        builder.HasIndex(job => new { job.OrganizationId, job.PropertyId })
            .IsUnique()
            .HasFilter("\"Status\" IN ('Queued', 'Running')");

        builder.HasOne(job => job.Organization)
            .WithMany()
            .HasForeignKey(job => job.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(job => job.Property)
            .WithMany()
            .HasForeignKey(job => new { job.PropertyId, job.OrganizationId })
            .HasPrincipalKey(property => new { property.Id, property.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(job => job.RequestedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(job => job.MasterVideoProductionPlan)
            .WithMany()
            .HasForeignKey(job => new
            {
                job.MasterVideoProductionPlanId,
                job.PropertyId,
                job.OrganizationId,
            })
            .HasPrincipalKey(plan => new { plan.Id, plan.PropertyId, plan.OrganizationId })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(job => job.VideoNarration)
            .WithMany()
            .HasForeignKey(job => new
            {
                job.VideoNarrationId,
                job.PropertyId,
                job.OrganizationId,
            })
            .HasPrincipalKey(narration => new { narration.Id, narration.PropertyId, narration.OrganizationId })
            .OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(job => job.Deliverables).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
