using ListingStudio.Domain.Properties;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ListingStudio.Infrastructure.Persistence.Configurations;

public sealed class WalkthroughTransitionConfiguration : IEntityTypeConfiguration<WalkthroughTransition>
{
    public void Configure(EntityTypeBuilder<WalkthroughTransition> builder)
    {
        builder.ToTable("WalkthroughTransitions");
        builder.HasKey(transition => transition.Id);
        builder.Property(transition => transition.GenerationVersion).HasMaxLength(100).IsRequired();
        builder.Property(transition => transition.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(transition => transition.ProviderRequestId).HasMaxLength(200);
        builder.Property(transition => transition.LastError).HasMaxLength(WalkthroughTransition.MaximumErrorLength);
        builder.Property(transition => transition.Provider).HasMaxLength(100);
        builder.Property(transition => transition.Model).HasMaxLength(200);
        builder.Property(transition => transition.AssetPath).HasMaxLength(1_024);
        builder.Property(transition => transition.ContentType).HasMaxLength(100);
        builder.HasIndex(transition => new
        {
            transition.OrganizationId,
            transition.PropertyId,
            transition.FromPropertyMediaId,
            transition.ToPropertyMediaId,
            transition.GenerationVersion,
        }).IsUnique();
        builder.HasIndex(transition => new { transition.Status, transition.NextCheckAtUtc })
            .HasDatabaseName("IX_WalkthroughTransitions_Queue");

        builder.HasOne(transition => transition.Organization)
            .WithMany()
            .HasForeignKey(transition => transition.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(transition => transition.Property)
            .WithMany()
            .HasForeignKey(transition => new { transition.PropertyId, transition.OrganizationId })
            .HasPrincipalKey(property => new { property.Id, property.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<PropertyMedia>()
            .WithMany()
            .HasForeignKey(transition => new
            {
                transition.FromPropertyMediaId,
                transition.PropertyId,
                transition.OrganizationId,
            })
            .HasPrincipalKey(media => new { media.Id, media.PropertyId, media.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<PropertyMedia>()
            .WithMany()
            .HasForeignKey(transition => new
            {
                transition.ToPropertyMediaId,
                transition.PropertyId,
                transition.OrganizationId,
            })
            .HasPrincipalKey(media => new { media.Id, media.PropertyId, media.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
