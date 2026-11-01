using ListingStudio.Domain.Properties;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ListingStudio.Infrastructure.Persistence.Configurations;

public sealed class PropertyMediaRoleAssignmentConfiguration : IEntityTypeConfiguration<PropertyMediaRoleAssignment>
{
    public void Configure(EntityTypeBuilder<PropertyMediaRoleAssignment> builder)
    {
        builder.ToTable("PropertyMediaRoleAssignments", table => table.HasCheckConstraint(
            "CK_PropertyMediaRoleAssignments_OneTarget",
            "(\"PropertyMediaId\" IS NULL) <> (\"PropertyVideoId\" IS NULL)"));
        builder.HasKey(assignment => assignment.Id);
        builder.Property(assignment => assignment.Id).ValueGeneratedNever();
        builder.Property(assignment => assignment.Role).HasConversion<string>().HasMaxLength(40).IsRequired();
        builder.Property(assignment => assignment.Decision).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(assignment => assignment.SetByUserId).HasMaxLength(450).IsRequired();
        builder.Ignore(assignment => assignment.TargetId);
        builder.HasIndex(assignment => new { assignment.OrganizationId, assignment.PropertyId, assignment.SetAtUtc });
        builder.HasIndex(assignment => assignment.PropertyMediaId);
        builder.HasIndex(assignment => assignment.PropertyVideoId);

        builder.HasOne<ListingProperty>()
            .WithMany()
            .HasForeignKey(assignment => new { assignment.PropertyId, assignment.OrganizationId })
            .HasPrincipalKey(property => new { property.Id, property.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<PropertyMedia>()
            .WithMany()
            .HasForeignKey(assignment => assignment.PropertyMediaId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<PropertyVideo>()
            .WithMany()
            .HasForeignKey(assignment => assignment.PropertyVideoId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class ListingCreativeClaimConfiguration : IEntityTypeConfiguration<ListingCreativeClaim>
{
    public void Configure(EntityTypeBuilder<ListingCreativeClaim> builder)
    {
        builder.ToTable("ListingCreativeClaims");
        builder.HasKey(claim => claim.Id);
        builder.Property(claim => claim.Id).ValueGeneratedNever();
        builder.Property(claim => claim.Kind).HasConversion<string>().HasMaxLength(40).IsRequired();
        builder.Property(claim => claim.PrimaryText).HasMaxLength(ListingCreativeClaim.MaximumTextLength);
        builder.Property(claim => claim.SecondaryText).HasMaxLength(ListingCreativeClaim.MaximumTextLength);
        builder.Property(claim => claim.ConfirmedByUserId).HasMaxLength(450).IsRequired();
        builder.HasIndex(claim => new { claim.OrganizationId, claim.PropertyId, claim.Kind, claim.ConfirmedAtUtc });

        builder.HasOne<ListingProperty>()
            .WithMany()
            .HasForeignKey(claim => new { claim.PropertyId, claim.OrganizationId })
            .HasPrincipalKey(property => new { property.Id, property.OrganizationId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
