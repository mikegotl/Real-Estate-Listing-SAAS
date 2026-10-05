namespace ListingStudio.Infrastructure.Persistence.Configurations;

using ListingStudio.Domain.Billing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class CampaignUsageRecordConfiguration : IEntityTypeConfiguration<CampaignUsageRecord>
{
    public void Configure(EntityTypeBuilder<CampaignUsageRecord> builder)
    {
        builder.ToTable("CampaignUsageRecords");
        builder.HasKey(record => record.Id);
        builder.HasIndex(record => record.CampaignGenerationJobId).IsUnique();
        builder.HasIndex(record => new { record.OrganizationId, record.PeriodStartUtc });
        builder.HasOne(record => record.Organization)
            .WithMany()
            .HasForeignKey(record => record.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(record => record.CampaignGenerationJob)
            .WithMany()
            .HasForeignKey(record => new { record.CampaignGenerationJobId, record.OrganizationId })
            .HasPrincipalKey(job => new { job.Id, job.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
