using ListingStudio.Domain.Leads;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class PropertyLeadTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CapturesConsentAtSubmissionAndTracksWorkflow()
    {
        var lead = CreateLead();

        Assert.Equal("buyer@example.com", lead.Email);
        Assert.True(lead.EmailMarketingConsent);
        Assert.False(lead.SmsMarketingConsent);
        Assert.Equal(LeadStatus.New, lead.Status);

        lead.UpdateWorkflow(LeadStatus.ShowingScheduled, Now.AddDays(2), Now.AddMinutes(5));

        Assert.Equal(LeadStatus.ShowingScheduled, lead.Status);
        Assert.Equal(Now.AddDays(2), lead.FollowUpAtUtc);
        Assert.Equal(Now.AddMinutes(5), lead.UpdatedAtUtc);
    }

    [Fact]
    public void RejectsInvalidContactAndConsentCombinations()
    {
        Assert.Throws<ArgumentException>(() => CreateLead(email: "not-an-email"));
        Assert.Throws<ArgumentException>(() => CreateLead(
            phone: null,
            preferredContact: LeadPreferredContact.Phone));
        Assert.Throws<ArgumentException>(() => CreateLead(
            phone: null,
            smsConsent: true));
    }

    [Fact]
    public void CapturePageUsesUnguessableNormalizedSlugAndBoundedHeadline()
    {
        var page = LeadCapturePage.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "ABCDEF0123456789ABCDEF0123456789",
            Now);

        page.Update("  Request a private tour  ", published: true, Now.AddMinutes(1));

        Assert.Equal("abcdef0123456789abcdef0123456789", page.PublicSlug);
        Assert.Equal("Request a private tour", page.Headline);
        Assert.True(page.IsPublished);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            page.Update(new string('x', LeadCapturePage.MaximumHeadlineLength + 1), true, Now));
    }

    private static PropertyLead CreateLead(
        string email = " Buyer@Example.com ",
        string? phone = "919-555-0100",
        LeadPreferredContact preferredContact = LeadPreferredContact.Email,
        bool smsConsent = false) => PropertyLead.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Alex Buyer",
            email,
            phone,
            preferredContact,
            "I would like to schedule a showing.",
            "facebook",
            "social",
            "property",
            emailMarketingConsent: true,
            smsMarketingConsent: smsConsent,
            "2026-10",
            Now);
}
