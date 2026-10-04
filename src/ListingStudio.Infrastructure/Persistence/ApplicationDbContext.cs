using ListingStudio.Domain.Organizations;
using ListingStudio.Domain.Properties;
using ListingStudio.Domain.Stories;
using ListingStudio.Domain.Videos;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace ListingStudio.Infrastructure.Persistence;

public sealed class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<Organization> Organizations => Set<Organization>();

    public DbSet<OrganizationMember> OrganizationMembers => Set<OrganizationMember>();

    public DbSet<ListingProperty> Properties => Set<ListingProperty>();

    public DbSet<PropertyMedia> PropertyMedia => Set<PropertyMedia>();

    public DbSet<PropertyStory> PropertyStories => Set<PropertyStory>();

    public DbSet<VideoProductionPlan> VideoProductionPlans => Set<VideoProductionPlan>();

    public DbSet<VideoNarration> VideoNarrations => Set<VideoNarration>();

    public DbSet<CampaignGenerationJob> CampaignGenerationJobs => Set<CampaignGenerationJob>();

    public DbSet<CampaignDeliverable> CampaignDeliverables => Set<CampaignDeliverable>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);
    }
}
