using ListingStudio.Domain.Campaigns;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ListingStudio.Infrastructure.Persistence.Configurations;

public sealed class VerticalCreativeTestSuiteConfiguration : IEntityTypeConfiguration<VerticalCreativeTestSuite>
{
    public void Configure(EntityTypeBuilder<VerticalCreativeTestSuite> builder)
    {
        builder.ToTable("VerticalCreativeTestSuites");
        builder.HasKey(suite => suite.Id);
        builder.Property(suite => suite.RequestedByUserId).HasMaxLength(450).IsRequired();
        builder.Property(suite => suite.Mode).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(suite => suite.Format).HasConversion<string>().HasMaxLength(32);
        builder.Property(suite => suite.RuleVersion).HasMaxLength(40).IsRequired();
        builder.Property(suite => suite.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(suite => suite.Error).HasMaxLength(VerticalCreativeTestSuite.MaximumErrorLength);
        builder.Ignore(suite => suite.IsActive);
        builder.Ignore(suite => suite.RunningCase);
        builder.Ignore(suite => suite.NextCase);
        builder.HasIndex(suite => new { suite.Status, suite.UpdatedAtUtc });
        builder.HasIndex(suite => new { suite.OrganizationId, suite.CreatedAtUtc });
        // One active creative test per workspace, like the render regression suite (whose limit is separate).
        builder.HasIndex(suite => suite.OrganizationId).IsUnique().HasFilter("\"Status\" IN ('Queued', 'Running')")
            .HasDatabaseName("IX_VerticalCreativeTestSuites_OrganizationId_Active");
        builder.HasMany(suite => suite.Cases).WithOne(item => item.Suite).HasForeignKey(item => item.SuiteId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(suite => suite.Cases).AutoInclude(false);
    }
}

public sealed class VerticalCreativeTestCaseConfiguration : IEntityTypeConfiguration<VerticalCreativeTestCase>
{
    public void Configure(EntityTypeBuilder<VerticalCreativeTestCase> builder)
    {
        builder.ToTable("VerticalCreativeTestCases");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Format).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(item => item.HookVariant).HasMaxLength(8).IsRequired();
        builder.Property(item => item.ReasonsJson).HasMaxLength(VerticalCreativeTestCase.MaximumReasonsJsonLength).IsRequired();
        builder.Property(item => item.RecipeVersion).HasMaxLength(CampaignVideoVariant.MaximumRecipeVersionLength).IsRequired();
        builder.Property(item => item.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(item => item.Error).HasMaxLength(VerticalCreativeTestSuite.MaximumErrorLength);
        builder.HasIndex(item => new { item.SuiteId, item.Index }).IsUnique();
        builder.HasIndex(item => item.CaseKey).IsUnique();
        builder.HasIndex(item => new { item.OrganizationId, item.CampaignGenerationJobId });
        builder.HasIndex(item => new { item.OrganizationId, item.CampaignDeliverableId });
    }
}
