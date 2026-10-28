using ListingStudio.Domain.Organizations;
using ListingStudio.Domain.Properties;
using ListingStudio.Domain.Stories;
using ListingStudio.Domain.Videos;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Domain.Billing;
using ListingStudio.Domain.Neighborhoods;
using ListingStudio.Domain.Notifications;
using ListingStudio.Domain.Administration;
using ListingStudio.Domain.Leads;
using ListingStudio.Domain.Feedback;
using ListingStudio.Domain.Help;
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

    public DbSet<PropertyListingSource> PropertyListingSources => Set<PropertyListingSource>();

    public DbSet<PropertyMedia> PropertyMedia => Set<PropertyMedia>();

    public DbSet<PropertyVideo> PropertyVideos => Set<PropertyVideo>();

    public DbSet<PropertyNarrationScript> PropertyNarrationScripts => Set<PropertyNarrationScript>();

    public DbSet<PropertyStory> PropertyStories => Set<PropertyStory>();

    public DbSet<VideoProductionPlan> VideoProductionPlans => Set<VideoProductionPlan>();

    public DbSet<VideoNarration> VideoNarrations => Set<VideoNarration>();

    public DbSet<GeneratedVideoClip> GeneratedVideoClips => Set<GeneratedVideoClip>();

    public DbSet<AgentAvatarVideo> AgentAvatarVideos => Set<AgentAvatarVideo>();

    public DbSet<WalkthroughTransition> WalkthroughTransitions => Set<WalkthroughTransition>();

    public DbSet<WalkthroughStopDirection> WalkthroughStopDirections => Set<WalkthroughStopDirection>();

    public DbSet<WalkthroughStopNarration> WalkthroughStopNarrations => Set<WalkthroughStopNarration>();

    public DbSet<PropertyPhotoFix> PropertyPhotoFixes => Set<PropertyPhotoFix>();

    public DbSet<CampaignGenerationJob> CampaignGenerationJobs => Set<CampaignGenerationJob>();

    public DbSet<CampaignDeliverable> CampaignDeliverables => Set<CampaignDeliverable>();

    public DbSet<CampaignStillPost> CampaignStillPosts => Set<CampaignStillPost>();

    public DbSet<CampaignVideoChatMessage> CampaignVideoChatMessages => Set<CampaignVideoChatMessage>();

    public DbSet<CampaignVideoRevision> CampaignVideoRevisions => Set<CampaignVideoRevision>();

    public DbSet<AgentPortrait> AgentPortraits => Set<AgentPortrait>();

    public DbSet<OrganizationBillingAccount> OrganizationBillingAccounts => Set<OrganizationBillingAccount>();

    public DbSet<CampaignUsageRecord> CampaignUsageRecords => Set<CampaignUsageRecord>();

    public DbSet<BillingWebhookReceipt> BillingWebhookReceipts => Set<BillingWebhookReceipt>();

    public DbSet<NeighborhoodInsight> NeighborhoodInsights => Set<NeighborhoodInsight>();

    public DbSet<UserNotification> UserNotifications => Set<UserNotification>();

    public DbSet<PlatformAdminAuditEvent> PlatformAdminAuditEvents => Set<PlatformAdminAuditEvent>();

    public DbSet<OperationalEvent> OperationalEvents => Set<OperationalEvent>();

    public DbSet<AiUsageEvent> AiUsageEvents => Set<AiUsageEvent>();

    public DbSet<PlatformMetricsReset> PlatformMetricsResets => Set<PlatformMetricsReset>();

    public DbSet<LeadCapturePage> LeadCapturePages => Set<LeadCapturePage>();

    public DbSet<PropertyLead> PropertyLeads => Set<PropertyLead>();

    public DbSet<LeadActivity> LeadActivities => Set<LeadActivity>();

    public DbSet<FeatureSuggestion> FeatureSuggestions => Set<FeatureSuggestion>();

    public DbSet<HelpChatMessage> HelpChatMessages => Set<HelpChatMessage>();

    public DbSet<AdminUserEmail> AdminUserEmails => Set<AdminUserEmail>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        RejectAuditMutation();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        RejectAuditMutation();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void RejectAuditMutation()
    {
        if (ChangeTracker.Entries<PlatformAdminAuditEvent>()
            .Any(entry => entry.State is EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException("Platform administrator audit events are append-only.");
        }

        if (ChangeTracker.Entries<PlatformMetricsReset>()
            .Any(entry => entry.State is EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException("Platform metric resets are append-only.");
        }
    }
}
