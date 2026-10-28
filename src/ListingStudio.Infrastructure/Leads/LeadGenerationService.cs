using System.Text;
using ListingStudio.Application.Audio;
using ListingStudio.Application.Leads;
using ListingStudio.Application.Properties;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Domain.Leads;
using ListingStudio.Domain.Notifications;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ListingStudio.Infrastructure.Leads;

public sealed class LeadGenerationService(
    ApplicationDbContext dbContext,
    IPropertyMediaStorage mediaStorage,
    ICampaignAssetStorage campaignStorage,
    TimeProvider timeProvider) : ILeadGenerationService
{
    private static readonly TimeSpan DuplicateWindow = TimeSpan.FromMinutes(2);

    public async Task<LeadCaptureSettings?> GetCaptureSettingsAsync(
        string userId,
        Guid propertyId,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        return await dbContext.LeadCapturePages.AsNoTracking()
            .Where(page => page.OrganizationId == organizationId && page.PropertyId == propertyId)
            .Select(page => new LeadCaptureSettings(
                page.PropertyId,
                page.Property.Address1,
                page.PublicSlug,
                page.Headline,
                page.IsPublished,
                page.UpdatedAtUtc))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<LeadCaptureSettings?> SaveCaptureSettingsAsync(
        string userId,
        Guid propertyId,
        string? headline,
        bool published,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        var property = await dbContext.Properties.AsNoTracking().SingleOrDefaultAsync(
            candidate => candidate.Id == propertyId
                && candidate.OrganizationId == organizationId
                && candidate.ArchivedAtUtc == null,
            cancellationToken);
        if (property is null)
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();
        var page = await dbContext.LeadCapturePages.SingleOrDefaultAsync(
            candidate => candidate.PropertyId == propertyId && candidate.OrganizationId == organizationId,
            cancellationToken);
        if (page is null)
        {
            page = LeadCapturePage.Create(organizationId, propertyId, Guid.NewGuid().ToString("N"), now);
            dbContext.LeadCapturePages.Add(page);
        }

        page.Update(headline, published, now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new LeadCaptureSettings(
            propertyId,
            property.Address1,
            page.PublicSlug,
            page.Headline,
            page.IsPublished,
            page.UpdatedAtUtc);
    }

    public async Task<PublicListingResult?> GetPublicListingAsync(
        string publicSlug,
        CancellationToken cancellationToken = default)
    {
        var slug = NormalizeSlug(publicSlug);
        var listing = await dbContext.LeadCapturePages.AsNoTracking()
            .Where(page => page.PublicSlug == slug
                && page.IsPublished
                && page.Property.ArchivedAtUtc == null)
            .Select(page => new
            {
                page.PublicSlug,
                page.OrganizationId,
                page.PropertyId,
                page.Headline,
                OrganizationName = page.Property.Organization.Name,
                Property = page.Property,
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (listing is null)
        {
            return null;
        }

        var hasCover = await dbContext.PropertyMedia.AsNoTracking().AnyAsync(media =>
            media.OrganizationId == listing.OrganizationId
            && media.PropertyId == listing.PropertyId
            && !media.IsInGallery
            && media.WalkthroughStop != null,
            cancellationToken);
        var hasVideo = await PublicVideoQuery(listing.OrganizationId, listing.PropertyId)
            .AnyAsync(cancellationToken);
        return new PublicListingResult(
            listing.PublicSlug,
            listing.OrganizationName,
            listing.Property.Address1,
            listing.Property.Address2,
            listing.Property.City,
            listing.Property.State,
            listing.Property.ZipCode,
            listing.Property.ListingPrice,
            listing.Property.Bedrooms,
            listing.Property.Bathrooms,
            listing.Property.SquareFeet,
            listing.Property.LotSize,
            listing.Property.YearBuilt,
            listing.Property.PropertyType.ToString(),
            listing.Property.Description,
            listing.Headline,
            hasCover,
            hasVideo);
    }

    public async Task<PublicLeadSubmissionResult> SubmitAsync(
        string publicSlug,
        PublicLeadInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!string.IsNullOrWhiteSpace(input.Website))
        {
            return PublicLeadSubmissionResult.Failure("The submission could not be accepted.");
        }

        var slug = NormalizeSlug(publicSlug);
        var page = await dbContext.LeadCapturePages
            .Include(candidate => candidate.Property)
            .SingleOrDefaultAsync(candidate => candidate.PublicSlug == slug
                && candidate.IsPublished
                && candidate.Property.ArchivedAtUtc == null,
                cancellationToken);
        if (page is null)
        {
            return PublicLeadSubmissionResult.Failure("This listing is not accepting inquiries.");
        }

        var now = timeProvider.GetUtcNow();
        var normalizedEmail = input.Email.Trim().ToLowerInvariant();
        var duplicateAfter = now.Subtract(DuplicateWindow);
        if (await dbContext.PropertyLeads.AsNoTracking().AnyAsync(candidate =>
            candidate.LeadCapturePageId == page.Id
            && candidate.Email == normalizedEmail
            && candidate.CreatedAtUtc >= duplicateAfter,
            cancellationToken))
        {
            return PublicLeadSubmissionResult.Failure("Your inquiry was already received. Please wait before sending another.");
        }

        PropertyLead lead;
        try
        {
            lead = PropertyLead.Create(
                page.OrganizationId,
                page.PropertyId,
                page.Id,
                input.Name,
                input.Email,
                input.Phone,
                input.PreferredContact,
                input.Message,
                input.Source,
                input.Medium,
                input.Campaign,
                input.EmailMarketingConsent,
                input.SmsMarketingConsent,
                ILeadGenerationService.ConsentVersion,
                now);
        }
        catch (ArgumentException exception)
        {
            return PublicLeadSubmissionResult.Failure(exception.Message);
        }

        dbContext.PropertyLeads.Add(lead);
        dbContext.LeadActivities.Add(LeadActivity.Create(
            page.OrganizationId,
            lead.Id,
            LeadActivityKind.Created,
            "Inquiry submitted from the public listing page.",
            null,
            now));
        var memberIds = await dbContext.OrganizationMembers.AsNoTracking()
            .Where(member => member.OrganizationId == page.OrganizationId)
            .Select(member => member.UserId)
            .ToListAsync(cancellationToken);
        foreach (var memberId in memberIds)
        {
            dbContext.UserNotifications.Add(UserNotification.Create(
                page.OrganizationId,
                memberId,
                page.PropertyId,
                UserNotificationKind.Lead,
                UserNotificationOutcome.Succeeded,
                $"New lead for {page.Property.Address1}",
                $"{lead.Name} submitted a property inquiry.",
                "leads",
                now));
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return PublicLeadSubmissionResult.Success(lead.Id);
    }

    public async Task<LeadDashboard> GetDashboardAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var query = dbContext.PropertyLeads.AsNoTracking()
            .Where(lead => lead.OrganizationId == organizationId);
        var leads = await query
            .OrderByDescending(lead => lead.CreatedAtUtc)
            .Select(lead => new LeadSummary(
                lead.Id,
                lead.PropertyId,
                lead.Property.Address1,
                lead.Name,
                lead.Email,
                lead.Phone,
                lead.PreferredContact,
                lead.Status,
                lead.Source,
                lead.FollowUpAtUtc,
                lead.CreatedAtUtc))
            .ToListAsync(cancellationToken);
        return new LeadDashboard(
            leads.Count,
            leads.Count(lead => lead.Status == LeadStatus.New),
            leads.Count(lead => lead.FollowUpAtUtc <= now && lead.Status is not (LeadStatus.Won or LeadStatus.Lost)),
            leads.Count(lead => lead.Status == LeadStatus.ShowingScheduled),
            leads.Count(lead => lead.Status == LeadStatus.Won),
            leads);
    }

    public async Task<LeadDetails?> GetLeadAsync(
        string userId,
        Guid leadId,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        return await dbContext.PropertyLeads.AsNoTracking()
            .Where(lead => lead.Id == leadId && lead.OrganizationId == organizationId)
            .Select(lead => new LeadDetails(
                lead.Id,
                lead.PropertyId,
                lead.Property.Address1,
                lead.Name,
                lead.Email,
                lead.Phone,
                lead.PreferredContact,
                lead.Message,
                lead.Status,
                lead.Source,
                lead.Medium,
                lead.Campaign,
                lead.EmailMarketingConsent,
                lead.SmsMarketingConsent,
                lead.ConsentVersion,
                lead.FollowUpAtUtc,
                lead.CreatedAtUtc,
                lead.Activities.OrderByDescending(activity => activity.CreatedAtUtc)
                    .Select(activity => new LeadActivityItem(activity.Kind, activity.Text, activity.CreatedAtUtc))
                    .ToList()))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<bool> UpdateWorkflowAsync(
        string userId,
        Guid leadId,
        LeadStatus status,
        DateTimeOffset? followUpAtUtc,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        var lead = await dbContext.PropertyLeads.SingleOrDefaultAsync(
            candidate => candidate.Id == leadId && candidate.OrganizationId == organizationId,
            cancellationToken);
        if (lead is null)
        {
            return false;
        }

        var previous = lead.Status;
        var now = timeProvider.GetUtcNow();
        lead.UpdateWorkflow(status, followUpAtUtc, now);
        dbContext.LeadActivities.Add(LeadActivity.Create(
            organizationId,
            lead.Id,
            LeadActivityKind.StatusChanged,
            previous == status ? $"Follow-up updated for {status}." : $"Status changed from {previous} to {status}.",
            userId,
            now));
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> AddNoteAsync(
        string userId,
        Guid leadId,
        string note,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        if (!await dbContext.PropertyLeads.AsNoTracking().AnyAsync(
            lead => lead.Id == leadId && lead.OrganizationId == organizationId,
            cancellationToken))
        {
            return false;
        }

        dbContext.LeadActivities.Add(LeadActivity.Create(
            organizationId,
            leadId,
            LeadActivityKind.Note,
            note,
            userId,
            timeProvider.GetUtcNow()));
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> DeleteAsync(
        string userId,
        Guid leadId,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        return await dbContext.PropertyLeads
            .Where(lead => lead.Id == leadId && lead.OrganizationId == organizationId)
            .ExecuteDeleteAsync(cancellationToken) > 0;
    }

    public async Task<LeadPublicAsset?> OpenCoverAsync(
        string publicSlug,
        CancellationToken cancellationToken = default)
    {
        var slug = NormalizeSlug(publicSlug);
        var media = await dbContext.LeadCapturePages.AsNoTracking()
            .Where(page => page.PublicSlug == slug && page.IsPublished && page.Property.ArchivedAtUtc == null)
            .SelectMany(page => dbContext.PropertyMedia
                .Where(candidate => candidate.OrganizationId == page.OrganizationId
                    && candidate.PropertyId == page.PropertyId
                    && !candidate.IsInGallery
                    && candidate.WalkthroughStop != null)
                .OrderBy(candidate => candidate.DisplayOrder)
                .Take(1))
            .Select(candidate => new { candidate.BlobPath, candidate.MimeType })
            .SingleOrDefaultAsync(cancellationToken);
        if (media is null)
        {
            return null;
        }

        var content = await mediaStorage.OpenReadAsync(media.BlobPath, cancellationToken);
        return content is null ? null : new LeadPublicAsset(content, media.MimeType);
    }

    public async Task<LeadPublicAsset?> OpenVideoAsync(
        string publicSlug,
        CancellationToken cancellationToken = default)
    {
        var slug = NormalizeSlug(publicSlug);
        var page = await dbContext.LeadCapturePages.AsNoTracking()
            .Where(candidate => candidate.PublicSlug == slug
                && candidate.IsPublished
                && candidate.Property.ArchivedAtUtc == null)
            .Select(candidate => new { candidate.OrganizationId, candidate.PropertyId })
            .SingleOrDefaultAsync(cancellationToken);
        if (page is null)
        {
            return null;
        }

        var video = await PublicVideoQuery(page.OrganizationId, page.PropertyId)
            .Select(deliverable => new { deliverable.AssetPath, deliverable.ContentType })
            .FirstOrDefaultAsync(cancellationToken);
        if (video?.AssetPath is null || video.ContentType is null)
        {
            return null;
        }

        var content = await campaignStorage.OpenReadAsync(video.AssetPath, cancellationToken);
        return content is null ? null : new LeadPublicAsset(content, video.ContentType);
    }

    public async Task<byte[]> ExportCsvAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        var dashboard = await GetDashboardAsync(userId, cancellationToken);
        var builder = new StringBuilder("Name,Email,Phone,Property,Status,Source,Follow up,Created\r\n");
        foreach (var lead in dashboard.Leads)
        {
            builder.AppendJoin(',',
                Csv(lead.Name),
                Csv(lead.Email),
                Csv(lead.Phone),
                Csv(lead.PropertyLabel),
                Csv(lead.Status.ToString()),
                Csv(lead.Source),
                Csv(lead.FollowUpAtUtc?.ToString("O")),
                Csv(lead.CreatedAtUtc.ToString("O")));
            builder.Append("\r\n");
        }

        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetBytes(builder.ToString());
    }

    private IQueryable<CampaignDeliverable> PublicVideoQuery(Guid organizationId, Guid propertyId) =>
        dbContext.CampaignDeliverables.AsNoTracking()
            .Where(deliverable => deliverable.OrganizationId == organizationId
                && deliverable.PropertyId == propertyId
                && deliverable.Status == CampaignDeliverableStatus.Rendered
                && deliverable.AssetPath != null
                && deliverable.CampaignGenerationJob.Status == CampaignGenerationStatus.Completed)
            .OrderByDescending(deliverable => deliverable.CampaignGenerationJob.CompletedAtUtc)
            .ThenBy(deliverable => deliverable.Kind == CampaignOutputKind.FacebookFeed ? 0
                : deliverable.Kind == CampaignOutputKind.Feature ? 1
                : deliverable.Kind == CampaignOutputKind.Hero ? 2
                : 3);

    private async Task<Guid> GetOrganizationIdAsync(string userId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        return await dbContext.OrganizationMembers.AsNoTracking()
            .Where(member => member.UserId == userId)
            .Select(member => (Guid?)member.OrganizationId)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new UnauthorizedAccessException("The user does not belong to an organization.");
    }

    private static string NormalizeSlug(string publicSlug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicSlug);
        return publicSlug.Trim().ToLowerInvariant();
    }

    private static string Csv(string? value)
    {
        var normalized = value ?? string.Empty;
        if (normalized.Length > 0 && normalized[0] is '=' or '+' or '-' or '@')
        {
            normalized = $"'{normalized}";
        }

        return $"\"{normalized.Replace("\"", "\"\"")}\"";
    }
}
