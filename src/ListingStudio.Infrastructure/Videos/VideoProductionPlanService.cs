using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ListingStudio.Application.Stories;
using ListingStudio.Application.Videos;
using ListingStudio.Domain.Properties;
using ListingStudio.Domain.Stories;
using ListingStudio.Domain.Videos;
using ListingStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ListingStudio.Infrastructure.Videos;

public sealed class VideoProductionPlanService(
    ApplicationDbContext dbContext,
    IVideoDirector director,
    IVideoProductionSpecificationValidator validator,
    IPropertyStoryGroundingValidator storyValidator) : IVideoProductionPlanService
{
    public async Task<VideoProductionPlanResult?> GetLatestAsync(
        string userId,
        Guid propertyId,
        RequestedDuration duration,
        VideoAspectRatio aspectRatio,
        CancellationToken cancellationToken = default)
    {
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        var plan = await dbContext.VideoProductionPlans
            .AsNoTracking()
            .Where(candidate => candidate.OrganizationId == organizationId
                && candidate.PropertyId == propertyId
                && candidate.RequestedDuration == duration
                && candidate.AspectRatio == aspectRatio)
            .OrderByDescending(candidate => candidate.Version)
            .FirstOrDefaultAsync(cancellationToken);
        return plan is null ? null : ToResult(plan, reused: false);
    }

    public async Task<VideoProductionPlanResult?> GenerateAsync(
        string userId,
        Guid propertyId,
        RequestedDuration duration,
        VideoAspectRatio aspectRatio,
        CancellationToken cancellationToken = default)
    {
        EnsureSupportedOutput(duration, aspectRatio);
        var organizationId = await GetOrganizationIdAsync(userId, cancellationToken);
        // Lock before source assembly/provider calls to prevent duplicate spend and racing property edits.
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var property = await dbContext.Properties
            .FromSqlInterpolated($"SELECT * FROM \"Properties\" WHERE \"Id\" = {propertyId} AND \"OrganizationId\" = {organizationId} FOR UPDATE")
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.Id == propertyId && candidate.OrganizationId == organizationId,
                cancellationToken);
        if (property is null)
        {
            return null;
        }

        if (property.IsArchived)
        {
            throw new InvalidOperationException("Archived properties cannot generate video production plans.");
        }

        var media = await dbContext.PropertyMedia
            .FromSqlInterpolated($"SELECT * FROM \"PropertyMedia\" WHERE \"PropertyId\" = {propertyId} AND \"OrganizationId\" = {organizationId} ORDER BY \"DisplayOrder\", \"Id\" FOR UPDATE")
            .AsNoTracking().ToArrayAsync(cancellationToken);
        if (media.Length == 0
            || media.Any(media => media.AnalysisStatus != PropertyMediaAnalysisStatus.Completed))
        {
            throw new InvalidOperationException(
                "Every property image must have completed analysis before video direction.");
        }

        var story = await dbContext.PropertyStories
            .AsNoTracking()
            .Where(candidate => candidate.OrganizationId == organizationId && candidate.PropertyId == propertyId)
            .OrderByDescending(candidate => candidate.Version)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("A grounded property story is required before video direction.");

        var organizationName = await dbContext.Organizations
            .AsNoTracking()
            .Where(organization => organization.Id == organizationId)
            .Select(organization => organization.Name)
            .SingleAsync(cancellationToken);
        var request = CreateRequest(property, media, story, organizationName, duration, aspectRatio);
        var storySource = new PropertyStoryGenerationRequest(request.VerifiedProperty,
            request.Media.Select(m => m.Analysis).ToArray(), new(organizationName, request.Brand.AgentName));
        if (!storyValidator.Validate(storySource, story.GetContent()).IsValid)
            throw new InvalidDataException("Stored property story no longer passes grounding against current verified facts. Generate a new story.");
        var fingerprint = CreateFingerprint(director.DirectorVersion, request);
        var existing = await FindByFingerprintAsync(organizationId, propertyId, fingerprint, cancellationToken);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return ToResult(existing, reused: true);
        }

        var editorialPlan = await director.DirectAsync(request, cancellationToken);
        var specification = new VideoProductionSpecification(
            "1.0",
            request.PropertyId,
            request.PropertyStory.Id,
            request.PropertyStory.Version,
            request.RequestedDuration,
            request.AspectRatio,
            request.Output,
            request.SafeZone,
            request.FactBindings,
            request.Brand,
            request.CallToAction,
            editorialPlan.Audio,
            editorialPlan.Scenes);
        var validation = validator.Validate(request, specification);
        if (!validation.IsValid)
        {
            throw new InvalidDataException(
                $"Generated video production specification failed validation: {string.Join(' ', validation.Errors)}");
        }

        var specificationJson = JsonSerializer.Serialize(specification, VideoSpecificationJson.Options);
        var latestVersion = await dbContext.VideoProductionPlans
            .Where(candidate => candidate.OrganizationId == organizationId
                && candidate.PropertyId == propertyId
                && candidate.RequestedDuration == duration
                && candidate.AspectRatio == aspectRatio)
            .Select(candidate => (int?)candidate.Version)
            .MaxAsync(cancellationToken) ?? 0;
        var plan = VideoProductionPlan.Create(
            organizationId,
            propertyId,
            story.Id,
            latestVersion + 1,
            duration,
            aspectRatio,
            specification.SchemaVersion,
            director.DirectorVersion,
            fingerprint,
            specificationJson);
        dbContext.VideoProductionPlans.Add(plan);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToResult(plan, reused: false);
    }

    private static VideoDirectionRequest CreateRequest(
        ListingProperty property,
        IReadOnlyList<PropertyMedia> propertyMedia,
        PropertyStory story,
        string organizationName,
        RequestedDuration duration,
        VideoAspectRatio aspectRatio)
    {
        var verified = new VerifiedPropertyData(
            property.Address1,
            property.Address2,
            property.City,
            property.State,
            property.ZipCode,
            property.ListingPrice,
            property.Bedrooms,
            property.Bathrooms,
            property.SquareFeet,
            property.LotSize,
            property.YearBuilt,
            property.PropertyType,
            property.Description,
            property.ListingStatus);
        var content = story.GetContent();
        var storyInput = new VideoPropertyStoryInput(
            story.Id,
            story.Version,
            new PropertyStoryContentSnapshot(
                content.CampaignTitle,
                content.OpeningHook,
                content.PropertyNarrative,
                content.Highlights,
                content.VoiceoverScript,
                content.ClosingCta));
        var media = propertyMedia
            .OrderBy(item => item.DisplayOrder)
            .Select(item => new VideoMediaInput(
                item.Id,
                item.Width,
                item.Height,
                ToObservation(item, item.GetAnalysis()!)))
            .ToArray();
        var brand = new VideoBrandPlan(
            null,
            null,
            null,
            null,
            null,
            null,
            "#17324D",
            "#F4F0E8");
        var bindings = CreateFactBindings(verified, content, organizationName);
        return new VideoDirectionRequest(
            property.Id,
            storyInput,
            verified,
            media,
            duration,
            aspectRatio,
            OutputFor(aspectRatio),
            SafeZoneFor(aspectRatio),
            bindings,
            brand,
            new GroundedText(content.ClosingCta, "story.closingCta"),
            new HashSet<Guid>(),
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal));
    }

    private static List<FactBinding> CreateFactBindings(
        VerifiedPropertyData property,
        PropertyStoryContent story,
        string organizationName)
    {
        var address = string.Join(", ", new[]
        {
            property.Address1,
            property.Address2,
            property.City,
            $"{property.State} {property.ZipCode}",
        }.Where(value => !string.IsNullOrWhiteSpace(value)));
        var facts = new List<FactBinding>
        {
            new("property.address.full", address, FactSource.VerifiedProperty, "Address1+Address2+City+State+ZipCode"),
            new("property.listingPrice", property.ListingPrice.ToString(property.ListingPrice == decimal.Truncate(property.ListingPrice) ? "C0" : "C2", CultureInfo.GetCultureInfo("en-US")),
                FactSource.VerifiedProperty, "ListingPrice"),
            new("property.bedBath", $"{property.Bedrooms} beds • {property.Bathrooms:0.##} baths",
                FactSource.VerifiedProperty, "Bedrooms+Bathrooms"),
            new("story.campaignTitle", story.CampaignTitle, FactSource.PropertyStory, "CampaignTitle"),
            new("story.openingHook", story.OpeningHook, FactSource.PropertyStory, "OpeningHook"),
            new("story.propertyNarrative", story.PropertyNarrative, FactSource.PropertyStory, "PropertyNarrative"),
            new("story.voiceover", story.VoiceoverScript, FactSource.PropertyStory, "VoiceoverScript"),
            new("story.closingCta", story.ClosingCta, FactSource.PropertyStory, "ClosingCta"),
            new("brand.organizationName", organizationName, FactSource.BrandKit, "Organization.Name"),
        };
        if (property.SquareFeet is { } squareFeet)
        {
            facts.Add(new FactBinding(
                "property.bedBathSquareFeet",
                $"{property.Bedrooms} beds • {property.Bathrooms:0.##} baths • {squareFeet:N0} sq ft",
                FactSource.VerifiedProperty,
                "Bedrooms+Bathrooms+SquareFeet"));
        }

        for (var index = 0; index < story.Highlights.Count; index++)
        {
            facts.Add(new FactBinding(
                $"story.highlight.{index + 1}",
                story.Highlights[index],
                FactSource.PropertyStory,
                $"Highlights[{index}]"));
        }

        return facts;
    }

    private static PropertyMediaObservation ToObservation(PropertyMedia media, PropertyMediaAnalysis analysis) => new(
        media.Id,
        media.DisplayOrder,
        analysis.Category,
        analysis.RoomType,
        analysis.QualityScore,
        analysis.HeroScore,
        analysis.IsExterior,
        analysis.IsInterior,
        analysis.ContainsPeople,
        analysis.PotentialProblems,
        analysis.Description,
        analysis.SuggestedDisplayOrder);

    private Task<VideoProductionPlan?> FindByFingerprintAsync(
        Guid organizationId,
        Guid propertyId,
        string fingerprint,
        CancellationToken cancellationToken) => dbContext.VideoProductionPlans
        .AsNoTracking()
        .SingleOrDefaultAsync(
            plan => plan.OrganizationId == organizationId
                && plan.PropertyId == propertyId
                && plan.SourceFingerprint == fingerprint,
            cancellationToken);

    private async Task<Guid> GetOrganizationIdAsync(string userId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var organizationId = await dbContext.OrganizationMembers
            .AsNoTracking()
            .Where(member => member.UserId == userId)
            .Select(member => (Guid?)member.OrganizationId)
            .FirstOrDefaultAsync(cancellationToken);
        return organizationId ?? throw new UnauthorizedAccessException("The user does not belong to an organization.");
    }

    private static string CreateFingerprint(string directorVersion, VideoDirectionRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directorVersion);
        var source = JsonSerializer.Serialize(new
        {
            schemaVersion = "1.0",
            directorVersion,
            request.PropertyId,
            request.PropertyStory,
            request.VerifiedProperty,
            request.Media,
            request.RequestedDuration,
            request.AspectRatio,
            request.Output,
            request.SafeZone,
            request.FactBindings,
            request.Brand,
            request.CallToAction,
            ApprovedGeneratedClipIds = request.ApprovedGeneratedClipIds.Order().ToArray(),
            ApprovedBrandAssetIds = request.ApprovedBrandAssetIds.Order(StringComparer.Ordinal).ToArray(),
            ApprovedMusicAssetIds = request.ApprovedMusicAssetIds.Order(StringComparer.Ordinal).ToArray(),
        }, VideoSpecificationJson.Options);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
    }

    private static VideoProductionPlanResult ToResult(VideoProductionPlan plan, bool reused)
    {
        var specification = JsonSerializer.Deserialize<VideoProductionSpecification>(
            plan.SpecificationJson,
            VideoSpecificationJson.Options)
            ?? throw new InvalidDataException("Stored video production specification is invalid.");
        return new VideoProductionPlanResult(
            plan.Id,
            plan.PropertyId,
            plan.PropertyStoryId,
            plan.Version,
            plan.DirectorVersion,
            specification,
            plan.CreatedAtUtc,
            reused);
    }

    private static VideoOutputProfile OutputFor(VideoAspectRatio aspectRatio) => aspectRatio switch
    {
        VideoAspectRatio.Landscape16By9 => new(1920, 1080, 30, "h264", "aac", "yuv420p", 48_000, 2),
        VideoAspectRatio.Vertical9By16 => new(1080, 1920, 30, "h264", "aac", "yuv420p", 48_000, 2),
        _ => throw new ArgumentOutOfRangeException(nameof(aspectRatio)),
    };

    private static NormalizedRect SafeZoneFor(VideoAspectRatio aspectRatio) => aspectRatio switch
    {
        VideoAspectRatio.Landscape16By9 => new(0.05m, 0.05m, 0.90m, 0.90m),
        VideoAspectRatio.Vertical9By16 => new(0.075m, 0.05m, 0.85m, 0.90m),
        _ => throw new ArgumentOutOfRangeException(nameof(aspectRatio)),
    };

    private static void EnsureSupportedOutput(RequestedDuration duration, VideoAspectRatio aspectRatio)
    {
        if (!Enum.IsDefined(duration) || !Enum.IsDefined(aspectRatio))
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "The requested output is not supported.");
        }
    }
}
