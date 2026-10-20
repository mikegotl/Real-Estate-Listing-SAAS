using ListingStudio.Domain.Leads;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ListingStudio.Infrastructure.Persistence.Configurations;

public sealed class LeadActivityConfiguration : IEntityTypeConfiguration<LeadActivity>
{
    public void Configure(EntityTypeBuilder<LeadActivity> builder)
    {
        builder.ToTable("LeadActivities");
        builder.HasKey(activity => activity.Id);
        builder.Property(activity => activity.Kind).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(activity => activity.Text).HasMaxLength(LeadActivity.MaximumTextLength).IsRequired();
        builder.Property(activity => activity.ActorUserId).HasMaxLength(450);
        builder.HasIndex(activity => new { activity.OrganizationId, activity.LeadId, activity.CreatedAtUtc });

        builder.HasOne(activity => activity.Lead)
            .WithMany(lead => lead.Activities)
            .HasForeignKey(activity => new { activity.LeadId, activity.OrganizationId })
            .HasPrincipalKey(lead => new { lead.Id, lead.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
