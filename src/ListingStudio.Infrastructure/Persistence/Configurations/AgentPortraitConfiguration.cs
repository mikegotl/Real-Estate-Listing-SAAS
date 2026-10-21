using ListingStudio.Domain.Campaigns;
using ListingStudio.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ListingStudio.Infrastructure.Persistence.Configurations;

public sealed class AgentPortraitConfiguration : IEntityTypeConfiguration<AgentPortrait>
{
    public void Configure(EntityTypeBuilder<AgentPortrait> builder)
    {
        builder.ToTable("AgentPortraits");
        builder.HasKey(portrait => portrait.Id);
        builder.HasAlternateKey(portrait => new { portrait.Id, portrait.OrganizationId });
        builder.Property(portrait => portrait.UserId).HasMaxLength(450).IsRequired();
        builder.Property(portrait => portrait.BlobPath).HasMaxLength(1_024).IsRequired();
        builder.Property(portrait => portrait.OriginalFilename).HasMaxLength(255).IsRequired();
        builder.Property(portrait => portrait.MimeType).HasMaxLength(100).IsRequired();
        builder.HasIndex(portrait => portrait.BlobPath).IsUnique();
        builder.HasIndex(portrait => new { portrait.OrganizationId, portrait.UserId, portrait.UpdatedAtUtc });
        builder.HasOne(portrait => portrait.Organization)
            .WithMany()
            .HasForeignKey(portrait => portrait.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(portrait => portrait.UserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
