using ListingStudio.Domain.Campaigns;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ListingStudio.Infrastructure.Persistence.Configurations;

public sealed class CampaignDistributionRecordConfiguration : IEntityTypeConfiguration<CampaignDistributionRecord>
{
    public void Configure(EntityTypeBuilder<CampaignDistributionRecord> builder)
    {
        builder.ToTable("CampaignDistributionRecords");
        builder.HasKey(record => record.Id);
        builder.Property(record => record.Platform).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(record => record.PostUrl).HasMaxLength(CampaignDistributionRecord.MaximumPostUrlLength).IsRequired();
        builder.Property(record => record.CreatedByUserId).HasMaxLength(450).IsRequired();
        // A post URL is recorded once per workspace, so a CSV row always matches exactly one video.
        builder.HasIndex(record => new { record.OrganizationId, record.PostUrl }).IsUnique();
        builder.HasIndex(record => new { record.OrganizationId, record.CampaignGenerationJobId });
        builder.HasIndex(record => new { record.OrganizationId, record.CampaignVideoVariantId });
        builder.HasOne<CampaignVideoVariant>().WithMany().HasForeignKey(record => record.CampaignVideoVariantId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<CampaignDeliverable>().WithMany().HasForeignKey(record => record.CampaignDeliverableId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class CampaignMetricSnapshotConfiguration : IEntityTypeConfiguration<CampaignMetricSnapshot>
{
    public void Configure(EntityTypeBuilder<CampaignMetricSnapshot> builder)
    {
        builder.ToTable("CampaignMetricSnapshots");
        builder.HasKey(snapshot => snapshot.Id);
        builder.Property(snapshot => snapshot.AverageWatchSeconds).HasPrecision(8, 2);
        builder.Property(snapshot => snapshot.CompletionRate).HasPrecision(6, 4);
        builder.Property(snapshot => snapshot.Source).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(snapshot => snapshot.EnteredByUserId).HasMaxLength(450).IsRequired();
        builder.Ignore(snapshot => snapshot.Values);
        // The same reading (post and capture second) can only be stored once.
        builder.HasIndex(snapshot => new { snapshot.DistributionRecordId, snapshot.CapturedAtUtc }).IsUnique();
        builder.HasIndex(snapshot => snapshot.OrganizationId);
        builder.HasOne<CampaignDistributionRecord>().WithMany().HasForeignKey(snapshot => snapshot.DistributionRecordId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class VerticalRuleEvaluationReportConfiguration : IEntityTypeConfiguration<VerticalRuleEvaluationReport>
{
    public void Configure(EntityTypeBuilder<VerticalRuleEvaluationReport> builder)
    {
        builder.ToTable("VerticalRuleEvaluationReports");
        builder.HasKey(report => report.Id);
        builder.Property(report => report.CreatedByUserId).HasMaxLength(450).IsRequired();
        builder.Property(report => report.BaseRuleVersion).HasMaxLength(40).IsRequired();
        builder.Property(report => report.CalculationVersion).HasMaxLength(40).IsRequired();
        builder.Property(report => report.SegmentsJson).IsRequired();
        builder.Property(report => report.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(report => report.PromotedRuleVersion).HasMaxLength(40);
        builder.Property(report => report.PromotedByUserId).HasMaxLength(450);
        builder.HasIndex(report => new { report.OrganizationId, report.CreatedAtUtc });
    }
}
