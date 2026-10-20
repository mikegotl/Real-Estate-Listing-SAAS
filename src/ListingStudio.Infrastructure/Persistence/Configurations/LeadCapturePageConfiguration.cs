using ListingStudio.Domain.Leads;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ListingStudio.Infrastructure.Persistence.Configurations;

public sealed class LeadCapturePageConfiguration : IEntityTypeConfiguration<LeadCapturePage>
{
    public void Configure(EntityTypeBuilder<LeadCapturePage> builder)
    {
        builder.ToTable("LeadCapturePages");
        builder.HasKey(page => page.Id);
        builder.HasAlternateKey(page => new { page.Id, page.OrganizationId });
        builder.Property(page => page.PublicSlug).HasMaxLength(32).IsRequired();
        builder.Property(page => page.Headline).HasMaxLength(LeadCapturePage.MaximumHeadlineLength);
        builder.HasIndex(page => page.PublicSlug).IsUnique();
        builder.HasIndex(page => new { page.OrganizationId, page.PropertyId }).IsUnique();

        builder.HasOne(page => page.Property)
            .WithMany()
            .HasForeignKey(page => new { page.PropertyId, page.OrganizationId })
            .HasPrincipalKey(property => new { property.Id, property.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
