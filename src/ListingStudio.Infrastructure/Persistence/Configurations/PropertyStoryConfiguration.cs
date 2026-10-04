using ListingStudio.Domain.Stories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ListingStudio.Infrastructure.Persistence.Configurations;

public sealed class PropertyStoryConfiguration : IEntityTypeConfiguration<PropertyStory>
{
    public void Configure(EntityTypeBuilder<PropertyStory> builder)
    {
        builder.ToTable("PropertyStories");
        builder.HasKey(story => story.Id);

        builder.Property(story => story.GenerationVersion).HasMaxLength(100).IsRequired();
        builder.Property(story => story.SourceFingerprint).HasMaxLength(64).IsRequired();
        builder.Property(story => story.CampaignTitle).HasMaxLength(200).IsRequired();
        builder.Property(story => story.OpeningHook).HasMaxLength(500).IsRequired();
        builder.Property(story => story.PropertyNarrative).HasMaxLength(4_000).IsRequired();
        builder.Ignore(story => story.Highlights);
        builder.Property<string[]>("highlights")
            .HasColumnName("Highlights")
            .HasColumnType("text[]")
            .IsRequired();
        builder.Property(story => story.VoiceoverScript).HasMaxLength(6_000).IsRequired();
        builder.Property(story => story.ClosingCta).HasMaxLength(500).IsRequired();
        builder.Property(story => story.SocialCaptionLong).HasMaxLength(2_200).IsRequired();
        builder.Property(story => story.SocialCaptionShort).HasMaxLength(500).IsRequired();

        builder.HasIndex(story => new { story.OrganizationId, story.PropertyId, story.Version }).IsUnique();
        builder.HasIndex(story => new { story.OrganizationId, story.PropertyId, story.SourceFingerprint }).IsUnique();

        builder
            .HasOne(story => story.Organization)
            .WithMany(organization => organization.PropertyStories)
            .HasForeignKey(story => story.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder
            .HasOne(story => story.Property)
            .WithMany(property => property.Stories)
            .HasForeignKey(story => new { story.PropertyId, story.OrganizationId })
            .HasPrincipalKey(property => new { property.Id, property.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
