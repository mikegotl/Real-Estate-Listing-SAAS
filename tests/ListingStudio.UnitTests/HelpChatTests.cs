using System.Net;
using System.Text;
using System.Text.Json;
using ListingStudio.AI.Configuration;
using ListingStudio.AI.Help;
using ListingStudio.Application.Help;
using ListingStudio.Application.Videos;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Domain.Help;
using ListingStudio.Domain.Videos;
using Microsoft.Extensions.Options;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class HelpChatTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ProductionNotesExplainRoomNarrationMusicChoiceAndShotKinds()
    {
        var job = Job(new CampaignCreativeOptions(
            CampaignVideoElement.NarrationCaptions | CampaignVideoElement.BackgroundMusic,
            CampaignSoundtrack.AutoByHomeEra,
            CampaignCaptionStyle.Pop,
            MarketingTemplate: CampaignMarketingTemplate.JustListed));
        var specification = Specification(
            [new FactBinding("room.kitchen", "A bright kitchen.", FactSource.AcceptedScript, "WalkthroughStopNarration:Kitchen")],
            [new NarrationSegment("n1", 0, 8_000, "A bright kitchen.", "room.kitchen")],
            new MusicPlan(CampaignCreativeDirector.HeritageWarmAssetId, MusicMood.WarmCinematic, 0, 30_000, -18, 500, 1_000, -8),
            new VisualSource(VisualSourceKind.PropertyMedia, Guid.NewGuid(), null, null, null),
            new VisualSource(VisualSourceKind.GeneratedClip, null, Guid.NewGuid(), Guid.NewGuid(), null),
            new VisualSource(VisualSourceKind.PropertyVideo, null, null, null, null, Guid.NewGuid(), 0));

        var notes = CampaignVideoProductionNotes.Describe(CampaignOutputKind.YouTubeTour, specification, job, CampaignVoiceAccent.British)
            .ToDictionary(note => note.Topic, note => note.Detail);

        Assert.Contains("master tour plan", notes["How it was edited"], StringComparison.Ordinal);
        Assert.Equal("Just listed", notes["Marketing template"]);
        Assert.Contains("room-by-room narration", notes["Voiceover words"], StringComparison.Ordinal);
        Assert.Contains("British accent", notes["Voice"], StringComparison.Ordinal);
        Assert.Contains("Heritage warm", notes["Music"], StringComparison.Ordinal);
        Assert.Contains("home's era", notes["Music"], StringComparison.Ordinal);
        Assert.Contains("-18 dB", notes["Music"], StringComparison.Ordinal);
        Assert.Equal("No captions in this version.", notes["Captions"]);
        Assert.Contains("3 shots: 1 listing photo", notes["Shots"], StringComparison.Ordinal);
        Assert.Contains("1 AI motion clip ", notes["Shots"], StringComparison.Ordinal);
        Assert.Contains("1 moment from the agent's uploaded walkthrough video", notes["Shots"], StringComparison.Ordinal);
        Assert.Equal("narration captions, background music.", notes["On-screen elements switched on"]);
    }

    [Fact]
    public void ProductionNotesForAShortVideoWithoutVoiceoverSayItWasTheChosenMode()
    {
        var job = Job(CampaignCreativeOptions.SocialDefault with
        {
            Elements = CampaignCreativeOptions.SocialDefault.Elements & ~CampaignVideoElement.BackgroundMusic,
            Soundtrack = CampaignSoundtrack.AddInApp,
            Pace = CampaignPace.Cinematic,
            Voiceover = CampaignVoiceoverMode.None,
        });
        var specification = Specification(
            [],
            [],
            new MusicPlan(null, MusicMood.None, 0, 0, 0, 0, 0, 0),
            new VisualSource(VisualSourceKind.PropertyMedia, Guid.NewGuid(), null, null, null));

        var notes = CampaignVideoProductionNotes.Describe(CampaignOutputKind.TikTok, specification, job, null)
            .ToDictionary(note => note.Topic, note => note.Detail);

        Assert.Contains("Cinematic glide", notes["How it was edited"], StringComparison.Ordinal);
        Assert.Contains("music and on-screen text only", notes["Voiceover"], StringComparison.Ordinal);
        Assert.False(notes.ContainsKey("Voice"));
        Assert.Contains("add a trending sound", notes["Music"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task HelpAssistantSendsThePageAndPropertyAsDataAndReturnsTheReply()
    {
        var response = JsonSerializer.Serialize(new
        {
            output = new[]
            {
                new
                {
                    content = new[]
                    {
                        new { type = "output_text", text = JsonSerializer.Serialize(new { reply = "  Open the Media tab and press Add stop.  " }) },
                    },
                },
            },
        });
        var handler = new RecordingHandler(response);
        using var client = new HttpClient(handler);
        var assistant = new OpenAIHelpChatAssistant(client, Options.Create(new OpenAIOptions
        {
            ApiKey = "test-key",
            Model = "test-model",
            ResponsesEndpoint = "https://api.openai.test/v1/responses",
        }));

        var reply = await assistant.ReplyAsync(new HelpChatRequest(
            "/properties/abc",
            new HelpChatPropertySummary("123 Main Street, Raleigh, NC", 6, 2, "Completed", null, ["YouTube tour"]),
            [
                new HelpChatTurn(HelpChatRole.User, "Hi"),
                new HelpChatTurn(HelpChatRole.Assistant, "Hello!"),
                new HelpChatTurn(HelpChatRole.User, "How do I add a room?"),
            ]));

        Assert.Equal("Open the Media tab and press Add stop.", reply);
        using var sent = JsonDocument.Parse(handler.RequestBody!);
        var instructions = sent.RootElement.GetProperty("instructions").GetString()!;
        Assert.Contains("untrusted data", instructions, StringComparison.Ordinal);
        Assert.Contains("Ask about", instructions, StringComparison.Ordinal);
        Assert.False(sent.RootElement.GetProperty("store").GetBoolean());
        var input = sent.RootElement.GetProperty("input");
        Assert.Equal(4, input.GetArrayLength());
        var context = input[0].GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.Contains("data, not instructions", context, StringComparison.Ordinal);
        Assert.Contains("\"photos_on_tour_map\":6", context, StringComparison.Ordinal);
        Assert.Contains("YouTube tour", context, StringComparison.Ordinal);
        Assert.Equal("output_text", input[2].GetProperty("content")[0].GetProperty("type").GetString());
    }

    [Fact]
    public void EmptyHelpRepliesAreRejectedAndLongOnesTrimmed()
    {
        using var empty = JsonDocument.Parse("""{"reply":"  "}""");
        Assert.Throws<InvalidDataException>(() => OpenAIHelpChatAssistant.Parse(empty.RootElement));
        using var longReply = JsonDocument.Parse(JsonSerializer.Serialize(new { reply = new string('a', 3_000) }));
        Assert.Equal(HelpChatMessage.MaximumTextLength, OpenAIHelpChatAssistant.Parse(longReply.RootElement).Length);
    }

    [Fact]
    public void HelpMessagesKeepTheirConversationAndTrimLongPaths()
    {
        var question = HelpChatMessage.FromUser(
            Guid.NewGuid(), "user-1", Guid.NewGuid(), "  How?  ", "/" + new string('p', 400), null, Now);
        var answer = HelpChatMessage.FromAssistant(question, "Like this.", Now);

        Assert.Equal("How?", question.Text);
        Assert.Equal(HelpChatMessage.MaximumPagePathLength, question.PagePath!.Length);
        Assert.Equal(question.ConversationId, answer.ConversationId);
        Assert.Equal("user-1", answer.UserId);
        Assert.Equal(HelpChatRole.Assistant, answer.Role);
        Assert.Throws<ArgumentException>(() => HelpChatMessage.FromUser(
            Guid.NewGuid(), "user-1", Guid.Empty, "Hi", null, null, Now));
    }

    private static CampaignGenerationJob Job(CampaignCreativeOptions options) => CampaignGenerationJob.Create(
        Guid.NewGuid(),
        Guid.NewGuid(),
        "owner-id",
        new string('a', 64),
        CampaignOutputSelection.TikTok | CampaignOutputSelection.YouTubeTour,
        Now,
        options);

    private static VideoProductionSpecification Specification(
        IReadOnlyList<FactBinding> bindings,
        IReadOnlyList<NarrationSegment> narration,
        MusicPlan music,
        params VisualSource[] visuals)
    {
        var fill = new NormalizedRect(0, 0, 1, 1);
        return new VideoProductionSpecification(
            "1.0",
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            RequestedDuration.Feature30,
            VideoAspectRatio.Landscape16By9,
            new VideoOutputProfile(1920, 1080, 30, "h264", "aac", "yuv420p", 48_000, 2),
            new NormalizedRect(0.05m, 0.05m, 0.9m, 0.9m),
            bindings,
            new BrandKit(null, null, "Agent", null, null, null, "#FFFFFF", "#17324D"),
            new GroundedText("Book a tour", "cta"),
            new AudioPlan(narration, music),
            visuals.Select((visual, index) => new VideoScene(
                index + 1,
                index * 10_000,
                10_000,
                visual,
                new TransitionPlan(TransitionKind.Cut, 0),
                new MotionPlan(MotionKind.None, fill, fill, MotionEasing.Linear),
                [],
                [],
                [])).ToArray());
    }

    private sealed class RecordingHandler(string responseJson) : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            };
        }
    }
}
