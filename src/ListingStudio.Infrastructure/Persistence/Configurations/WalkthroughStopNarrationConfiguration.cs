using ListingStudio.Domain.Properties;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ListingStudio.Infrastructure.Persistence.Configurations;

public sealed class WalkthroughStopNarrationConfiguration : IEntityTypeConfiguration<WalkthroughStopNarration>
{
    public void Configure(EntityTypeBuilder<WalkthroughStopNarration> builder)
    {
        builder.ToTable("WalkthroughStopNarrations");
        builder.HasKey(narration => narration.Id);
        builder.Property(narration => narration.Stop).HasMaxLength(PropertyMedia.MaximumWalkthroughStopLength).IsRequired();
        builder.Property(narration => narration.Text).HasMaxLength(WalkthroughStopNarration.MaximumTextLength).IsRequired();
        builder.Property(narration => narration.UpdatedByUserId).HasMaxLength(450).IsRequired();
        builder.HasIndex(narration => new { narration.OrganizationId, narration.PropertyId, narration.Stop }).IsUnique();

        builder.HasOne(narration => narration.Organization)
            .WithMany()
            .HasForeignKey(narration => narration.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(narration => narration.Property)
            .WithMany()
            .HasForeignKey(narration => new { narration.PropertyId, narration.OrganizationId })
            .HasPrincipalKey(property => new { property.Id, property.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
