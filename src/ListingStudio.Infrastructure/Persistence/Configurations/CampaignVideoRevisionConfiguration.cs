using ListingStudio.Domain.Campaigns;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ListingStudio.Infrastructure.Persistence.Configurations;

public sealed class CampaignVideoChatMessageConfiguration : IEntityTypeConfiguration<CampaignVideoChatMessage>
{
    public void Configure(EntityTypeBuilder<CampaignVideoChatMessage> builder)
    {
        builder.ToTable("CampaignVideoChatMessages");
        builder.HasKey(message => message.Id);
        builder.Property(message => message.UserId).HasMaxLength(450);
        builder.Property(message => message.Role).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(message => message.Text).HasMaxLength(CampaignVideoChatMessage.MaximumTextLength).IsRequired();
        builder.Property(message => message.Kind).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(message => message.EditsJson).HasColumnType("jsonb");
        builder.HasIndex(message => new { message.OrganizationId, message.CampaignDeliverableId, message.CreatedAtUtc });
        // Kept for the administrator review page, so there is no foreign key that could delete it with its video.
        builder.HasIndex(message => message.CreatedAtUtc);
    }
}

public sealed class CampaignVideoRevisionConfiguration : IEntityTypeConfiguration<CampaignVideoRevision>
{
    public void Configure(EntityTypeBuilder<CampaignVideoRevision> builder)
    {
        builder.ToTable("CampaignVideoRevisions");
        builder.HasKey(revision => revision.Id);
        builder.Property(revision => revision.RequestedByUserId).HasMaxLength(450).IsRequired();
        builder.Property(revision => revision.EditsJson).HasColumnType("jsonb").IsRequired();
        builder.Property(revision => revision.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(revision => revision.LastError).HasMaxLength(500);
        builder.Property(revision => revision.PreviousSpecificationJson).HasColumnType("jsonb");
        builder.Property(revision => revision.PreviousAssetPath).HasMaxLength(1_024);
        builder.Property(revision => revision.PreviousContentType).HasMaxLength(100);
        builder.HasIndex(revision => new { revision.Status, revision.NextAttemptAtUtc });
        builder.HasIndex(revision => new { revision.OrganizationId, revision.CampaignDeliverableId, revision.CreatedAtUtc });
        builder.HasIndex(revision => revision.ChatMessageId);
        builder.HasOne<CampaignDeliverable>()
            .WithMany()
            .HasForeignKey(revision => revision.CampaignDeliverableId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<CampaignVideoChatMessage>()
            .WithMany()
            .HasForeignKey(revision => revision.ChatMessageId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
