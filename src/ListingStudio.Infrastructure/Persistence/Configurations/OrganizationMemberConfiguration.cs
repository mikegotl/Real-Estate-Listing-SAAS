using ListingStudio.Domain.Organizations;
using ListingStudio.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ListingStudio.Infrastructure.Persistence.Configurations;

public sealed class OrganizationMemberConfiguration : IEntityTypeConfiguration<OrganizationMember>
{
    public void Configure(EntityTypeBuilder<OrganizationMember> builder)
    {
        builder.ToTable("OrganizationMembers");
        builder.HasKey(member => member.Id);
        builder.Property(member => member.UserId).HasMaxLength(450).IsRequired();
        builder.Property(member => member.Role).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.HasIndex(member => new { member.OrganizationId, member.UserId }).IsUnique();
        builder.HasIndex(member => member.UserId);

        builder
            .HasOne(member => member.Organization)
            .WithMany(organization => organization.Members)
            .HasForeignKey(member => member.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder
            .HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(member => member.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
