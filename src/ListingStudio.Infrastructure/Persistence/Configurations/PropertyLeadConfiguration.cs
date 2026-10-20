using ListingStudio.Domain.Leads;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ListingStudio.Infrastructure.Persistence.Configurations;

public sealed class PropertyLeadConfiguration : IEntityTypeConfiguration<PropertyLead>
{
    public void Configure(EntityTypeBuilder<PropertyLead> builder)
    {
        builder.ToTable("PropertyLeads");
        builder.HasKey(lead => lead.Id);
        builder.HasAlternateKey(lead => new { lead.Id, lead.OrganizationId });
        builder.Property(lead => lead.Name).HasMaxLength(PropertyLead.MaximumNameLength).IsRequired();
        builder.Property(lead => lead.Email).HasMaxLength(PropertyLead.MaximumEmailLength).IsRequired();
        builder.Property(lead => lead.Phone).HasMaxLength(PropertyLead.MaximumPhoneLength);
        builder.Property(lead => lead.PreferredContact).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(lead => lead.Message).HasMaxLength(PropertyLead.MaximumMessageLength).IsRequired();
        builder.Property(lead => lead.Source).HasMaxLength(PropertyLead.MaximumAttributionLength);
        builder.Property(lead => lead.Medium).HasMaxLength(PropertyLead.MaximumAttributionLength);
        builder.Property(lead => lead.Campaign).HasMaxLength(PropertyLead.MaximumAttributionLength);
        builder.Property(lead => lead.ConsentVersion).HasMaxLength(PropertyLead.MaximumConsentVersionLength).IsRequired();
        builder.Property(lead => lead.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.HasIndex(lead => new { lead.OrganizationId, lead.CreatedAtUtc });
        builder.HasIndex(lead => new { lead.OrganizationId, lead.Status, lead.FollowUpAtUtc });
        builder.HasIndex(lead => new { lead.LeadCapturePageId, lead.Email, lead.CreatedAtUtc });

        builder.HasOne(lead => lead.Property)
            .WithMany()
            .HasForeignKey(lead => new { lead.PropertyId, lead.OrganizationId })
            .HasPrincipalKey(property => new { property.Id, property.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(lead => lead.LeadCapturePage)
            .WithMany()
            .HasForeignKey(lead => new { lead.LeadCapturePageId, lead.OrganizationId })
            .HasPrincipalKey(page => new { page.Id, page.OrganizationId })
            .OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(lead => lead.Activities).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
