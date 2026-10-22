using ListingStudio.Domain.Feedback;
using ListingStudio.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ListingStudio.Infrastructure.Persistence.Configurations;

public sealed class FeatureSuggestionConfiguration : IEntityTypeConfiguration<FeatureSuggestion>
{
    public void Configure(EntityTypeBuilder<FeatureSuggestion> builder)
    {
        builder.ToTable("FeatureSuggestions");
        builder.HasKey(suggestion => suggestion.Id);
        builder.Property(suggestion => suggestion.SubmittedByUserId).HasMaxLength(450).IsRequired();
        builder.Property(suggestion => suggestion.Comment)
            .HasMaxLength(FeatureSuggestion.MaximumCommentLength)
            .IsRequired();
        builder.HasIndex(suggestion => new { suggestion.OrganizationId, suggestion.SubmittedAtUtc });
        builder.HasIndex(suggestion => new { suggestion.CampaignGenerationJobId, suggestion.OrganizationId });

        builder.HasOne(suggestion => suggestion.Organization)
            .WithMany()
            .HasForeignKey(suggestion => suggestion.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(suggestion => suggestion.Property)
            .WithMany()
            .HasForeignKey(suggestion => new { suggestion.PropertyId, suggestion.OrganizationId })
            .HasPrincipalKey(property => new { property.Id, property.OrganizationId })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(suggestion => suggestion.CampaignGenerationJob)
            .WithMany()
            .HasForeignKey(suggestion => new { suggestion.CampaignGenerationJobId, suggestion.OrganizationId })
            .HasPrincipalKey(job => new { job.Id, job.OrganizationId })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(suggestion => suggestion.SubmittedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
