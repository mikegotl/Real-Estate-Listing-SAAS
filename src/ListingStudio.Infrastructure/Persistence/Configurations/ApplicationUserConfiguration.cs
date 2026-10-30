using ListingStudio.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ListingStudio.Infrastructure.Persistence.Configurations;

public sealed class ApplicationUserConfiguration : IEntityTypeConfiguration<ApplicationUser>
{
    public void Configure(EntityTypeBuilder<ApplicationUser> builder)
    {
        builder.Property(user => user.IncludeAvatarInCampaigns)
            .HasDefaultValue(false)
            .IsRequired();
        builder.Property(user => user.RequiresPasswordChange)
            .HasDefaultValue(false)
            .IsRequired();
        builder.Property(user => user.TesterMonthlyCampaignLimit);
    }
}
