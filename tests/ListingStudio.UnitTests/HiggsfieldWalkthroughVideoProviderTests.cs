using System.Net;
using System.Text;
using System.Text.Json;
using ListingStudio.Application.Videos;
using ListingStudio.Domain.Properties;
using ListingStudio.Video.Configuration;
using ListingStudio.Video.Generation;
using Microsoft.Extensions.Options;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class HiggsfieldWalkthroughVideoProviderTests
{
    private static readonly byte[] ClipBytes = [0, 0, 0, 20, 102, 116, 121, 112];

    [Fact]
    public async Task SubmitUploadsBothPhotosAndRequestsAFirstToLastFrameClip()
    {
        var handler = new RoutingHandler(request => request.RequestUri!.AbsoluteUri switch
        {
            "https://api.higgsfield.test/files/generate-upload-url" => Json(new
            {
                upload_url = $"https://uploads.storage.test/{Guid.NewGuid():N}?signature=abc",
                public_url = $"https://cdn.storage.test/{Guid.NewGuid():N}.jpg",
                upload_headers = new Dictionary<string, string> { ["x-amz-acl"] = "private" },
            }),
            "https://api.higgsfield.test/kling-video/v3.0/std/image-to-video" => Json(new
            {
                status = "queued",
                request_id = "req_123",
                status_url = "https://api.higgsfield.test/requests/req_123/status",
            }),
            _ when request.RequestUri!.Host == "uploads.storage.test" => new HttpResponseMessage(HttpStatusCode.OK),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        using var client = new HttpClient(handler);
        var provider = CreateProvider(client);
        await using var start = new MemoryStream([1, 2, 3]);
        await using var end = new MemoryStream([4, 5, 6]);

        var requestId = await provider.SubmitAsync(new WalkthroughVideoSubmission(
            new WalkthroughVideoFrame(start, "image/jpeg"),
            new WalkthroughVideoFrame(end, "image/png"),
            "transition-1"));

        Assert.Equal("req_123", requestId);
        Assert.Equal(5, handler.Requests.Count);
        var apiRequests = handler.Requests.Where(request => request.Uri.Host == "api.higgsfield.test").ToArray();
        Assert.All(apiRequests, request => Assert.Equal("Key key-id:key-secret", request.Authorization));
        var uploads = handler.Requests.Where(request => request.Uri.Host == "uploads.storage.test").ToArray();
        Assert.Equal(2, uploads.Length);
        Assert.All(uploads, upload =>
        {
            Assert.Equal(HttpMethod.Put, upload.Method);
            Assert.Null(upload.Authorization);
            Assert.Equal("private", upload.Headers["x-amz-acl"]);
        });
        Assert.Equal([1, 2, 3], uploads[0].BodyBytes);
        Assert.Equal("image/png", uploads[1].ContentType);

        using var generation = JsonDocument.Parse(apiRequests[^1].Body);
        var root = generation.RootElement;
        Assert.StartsWith("https://cdn.storage.test/", root.GetProperty("image_url").GetString(), StringComparison.Ordinal);
        Assert.StartsWith("https://cdn.storage.test/", root.GetProperty("last_image_url").GetString(), StringComparison.Ordinal);
        Assert.NotEqual(root.GetProperty("image_url").GetString(), root.GetProperty("last_image_url").GetString());
        Assert.Equal(5, root.GetProperty("duration").GetInt32());
        Assert.Equal("off", root.GetProperty("sound").GetString());
        var prompt = root.GetProperty("prompt").GetString()!;
        Assert.Contains("arrive exactly on the last frame", prompt, StringComparison.Ordinal);
        Assert.Contains("Do not add, remove or redesign anything", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubmitSpellsOutTheMapDirectionSoTheClipTurnsTheRightWay()
    {
        var handler = new RoutingHandler(request => request.RequestUri!.ToString() switch
        {
            "https://api.higgsfield.test/files/generate-upload-url" => Json(new
            {
                upload_url = $"https://uploads.storage.test/{Guid.NewGuid():N}",
                public_url = $"https://cdn.storage.test/{Guid.NewGuid():N}.jpg",
            }),
            "https://api.higgsfield.test/kling-video/v3.0/std/image-to-video" => Json(new { status = "queued", request_id = "req_456" }),
            _ when request.RequestUri!.Host == "uploads.storage.test" => new HttpResponseMessage(HttpStatusCode.OK),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        using var client = new HttpClient(handler);
        await using var start = new MemoryStream([1]);
        await using var end = new MemoryStream([2]);

        await CreateProvider(client).SubmitAsync(new WalkthroughVideoSubmission(
            new WalkthroughVideoFrame(start, "image/jpeg"),
            new WalkthroughVideoFrame(end, "image/jpeg"),
            "transition-2",
            WalkthroughDirection.Right));

        using var generation = JsonDocument.Parse(handler.Requests[^1].Body);
        var prompt = generation.RootElement.GetProperty("prompt").GetString()!;
        Assert.StartsWith(HiggsfieldWalkthroughVideoProvider.Prompt, prompt, StringComparison.Ordinal);
        Assert.Contains("the next space is to the RIGHT", prompt, StringComparison.Ordinal);
        Assert.Contains("it must not turn left", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AerialIntroUploadsOnlyTheFrontPhotoAndUsesTheLowAerialMotionProfile()
    {
        var handler = new RoutingHandler(request => request.RequestUri!.ToString() switch
        {
            "https://api.higgsfield.test/files/generate-upload-url" => Json(new
            {
                upload_url = $"https://uploads.storage.test/{Guid.NewGuid():N}",
                public_url = $"https://cdn.storage.test/{Guid.NewGuid():N}.jpg",
            }),
            "https://api.higgsfield.test/kling-video/v3.0/std/image-to-video" => Json(new { status = "queued", request_id = "req_aerial" }),
            _ when request.RequestUri!.Host == "uploads.storage.test" => new HttpResponseMessage(HttpStatusCode.OK),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        using var client = new HttpClient(handler);
        await using var front = new MemoryStream([7, 8, 9]);
        var provider = CreateProvider(client);

        var requestId = await provider.SubmitAerialIntroAsync(new AerialIntroVideoSubmission(
            new WalkthroughVideoFrame(front, "image/jpeg"),
            50,
            "aerial-1"));

        Assert.Equal("req_aerial", requestId);
        Assert.Single(handler.Requests, request => request.Uri.Host == "uploads.storage.test");
        using var generation = JsonDocument.Parse(handler.Requests[^1].Body);
        var root = generation.RootElement;
        Assert.StartsWith("https://cdn.storage.test/", root.GetProperty("image_url").GetString(), StringComparison.Ordinal);
        Assert.False(root.TryGetProperty("last_image_url", out _));
        Assert.Equal(HiggsfieldWalkthroughVideoProvider.LowAerialIntroDurationSeconds, root.GetProperty("duration").GetInt32());
        Assert.Equal(HiggsfieldWalkthroughVideoProvider.AerialIntroPromptAdherence, root.GetProperty("cfg_scale").GetDecimal());
        var prompt = root.GetProperty("prompt").GetString()!;
        Assert.Contains("approximately 50-foot aerial viewpoint", prompt, StringComparison.Ordinal);
        Assert.Contains("Do not merely hover, drift a few feet", prompt, StringComparison.Ordinal);
        Assert.Contains("clearly above the roofline", prompt, StringComparison.Ordinal);
        Assert.Contains("Do not add a swimming pool", prompt, StringComparison.Ordinal);
        Assert.StartsWith("higgsfield/", provider.AerialIntroGenerationVersion, StringComparison.Ordinal);
        Assert.NotEqual(provider.GenerationVersion, provider.AerialIntroGenerationVersion);
    }

    [Fact]
    public async Task HighAerialIntroUsesALongerClipAndRequiresAVisibleScaleChange()
    {
        var handler = new RoutingHandler(request => request.RequestUri!.ToString() switch
        {
            "https://api.higgsfield.test/files/generate-upload-url" => Json(new
            {
                upload_url = $"https://uploads.storage.test/{Guid.NewGuid():N}",
                public_url = $"https://cdn.storage.test/{Guid.NewGuid():N}.jpg",
            }),
            "https://api.higgsfield.test/kling-video/v3.0/std/image-to-video" => Json(new { status = "queued", request_id = "req_high_aerial" }),
            _ when request.RequestUri!.Host == "uploads.storage.test" => new HttpResponseMessage(HttpStatusCode.OK),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        using var client = new HttpClient(handler);
        await using var front = new MemoryStream([9, 8, 7]);

        await CreateProvider(client).SubmitAerialIntroAsync(new AerialIntroVideoSubmission(
            new WalkthroughVideoFrame(front, "image/jpeg"),
            AerialIntroClip.HighHeightFeet,
            "aerial-high"));

        using var generation = JsonDocument.Parse(handler.Requests[^1].Body);
        var root = generation.RootElement;
        Assert.Equal(HiggsfieldWalkthroughVideoProvider.HighAerialIntroDurationSeconds, root.GetProperty("duration").GetInt32());
        var prompt = root.GetProperty("prompt").GetString()!;
        Assert.Contains("approximately 100-foot aerial viewpoint", prompt, StringComparison.Ordinal);
        Assert.Contains("entire roof", prompt, StringComparison.Ordinal);
        Assert.Contains("house should be much smaller", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void EachDirectionNamesOnlyItsOwnTurn()
    {
        Assert.Equal(HiggsfieldWalkthroughVideoProvider.Prompt, HiggsfieldWalkthroughVideoProvider.PromptFor(null));
        var left = HiggsfieldWalkthroughVideoProvider.PromptFor(WalkthroughDirection.Left);
        Assert.Contains("to the LEFT", left, StringComparison.Ordinal);
        Assert.DoesNotContain("to the RIGHT", left, StringComparison.Ordinal);
        Assert.Contains("UPSTAIRS", HiggsfieldWalkthroughVideoProvider.PromptFor(WalkthroughDirection.Upstairs), StringComparison.Ordinal);
        Assert.Contains("DOWNSTAIRS", HiggsfieldWalkthroughVideoProvider.PromptFor(WalkthroughDirection.Downstairs), StringComparison.Ordinal);
        Assert.Contains("straight ahead", HiggsfieldWalkthroughVideoProvider.PromptFor(WalkthroughDirection.Ahead), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FullTourUploadsOrderedTourMapFramesAndUsesKling4ThirtySecond1080pRequest()
    {
        var uploaded = 0;
        var handler = new RoutingHandler(request => request.RequestUri!.AbsoluteUri switch
        {
            "https://api.higgsfield.test/files/generate-upload-url" => Json(new
            {
                upload_url = $"https://uploads.storage.test/{++uploaded}",
                public_url = $"https://cdn.storage.test/frame-{uploaded}.jpg",
            }),
            "https://api.higgsfield.test/kling-video/v4.0/image-to-video" => Json(new
            {
                status = "queued",
                request_id = "req_full_tour",
            }),
            _ when request.RequestUri!.Host == "uploads.storage.test" => new HttpResponseMessage(HttpStatusCode.OK),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        using var client = new HttpClient(handler);
        await using var exterior = new MemoryStream([1]);
        await using var entry = new MemoryStream([2]);
        await using var kitchen = new MemoryStream([3]);
        var provider = CreateProvider(client);

        var submission = new FullTourVideoSubmission(
            [
                new(new WalkthroughVideoFrame(exterior, "image/jpeg"), "Exterior front", WalkthroughDirection.Ahead),
                new(new WalkthroughVideoFrame(entry, "image/jpeg"), "Entry foyer", WalkthroughDirection.Left),
                new(new WalkthroughVideoFrame(kitchen, "image/jpeg"), "Kitchen"),
            ],
            "campaign-1");
        var requestId = await provider.SubmitFullTourAsync(submission);

        Assert.Equal("req_full_tour", requestId);
        using var generation = JsonDocument.Parse(handler.Requests[^1].Body);
        var root = generation.RootElement;
        Assert.Equal("https://cdn.storage.test/frame-1.jpg", root.GetProperty("image_url").GetString());
        Assert.Equal("https://cdn.storage.test/frame-2.jpg", root.GetProperty("image_urls")[0].GetString());
        Assert.Equal("https://cdn.storage.test/frame-3.jpg", root.GetProperty("end_image_url").GetString());
        Assert.Equal(30, root.GetProperty("duration").GetInt32());
        Assert.Equal("1080p", root.GetProperty("resolution").GetString());
        Assert.False(root.TryGetProperty("sound", out _));
        Assert.False(root.TryGetProperty("aspect_ratio", out _));
        var prompt = root.GetProperty("prompt").GetString()!;
        Assert.Equal(provider.BuildFullTourPrompt(submission), prompt);
        Assert.Contains("exact order", prompt, StringComparison.Ordinal);
        Assert.Contains("Exterior front; next, straight ahead", prompt, StringComparison.Ordinal);
        Assert.Contains("Entry foyer; next, turn left", prompt, StringComparison.Ordinal);
        Assert.Contains("Keyframe 3: arrive in Kitchen", prompt, StringComparison.Ordinal);
        Assert.Equal(30_000, provider.FullTourDurationMs);
        Assert.Contains("full-tour", provider.FullTourGenerationVersion, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FullTourRejectsMoreThanTenFramesBeforeUploading()
    {
        var handler = new RoutingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(handler);
        var frames = Enumerable.Range(0, FullTourVideoPolicy.MaximumFrames + 1)
            .Select(index => new FullTourVideoFrame(
                new WalkthroughVideoFrame(new MemoryStream([checked((byte)index)]), "image/jpeg"),
                $"Stop {index}"))
            .ToArray();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => CreateProvider(client).SubmitFullTourAsync(
            new FullTourVideoSubmission(frames, "too-many")));

        Assert.Empty(handler.Requests);
        foreach (var frame in frames)
        {
            await frame.Frame.Content.DisposeAsync();
        }
    }

    [Fact]
    public async Task NativePresenterUsesAgentAtBothEndsAndTourMapPhotosInTheMiddle()
    {
        var uploaded = 0;
        var handler = new RoutingHandler(request => request.RequestUri!.AbsoluteUri switch
        {
            "https://api.higgsfield.test/files/generate-upload-url" => Json(new
            {
                upload_url = $"https://uploads.storage.test/{++uploaded}",
                public_url = $"https://cdn.storage.test/frame-{uploaded}.jpg",
            }),
            "https://api.higgsfield.test/kling-video/v4.0/image-to-video" => Json(new
            {
                status = "queued",
                request_id = "req_native_presenter",
            }),
            _ when request.RequestUri!.Host == "uploads.storage.test" => new HttpResponseMessage(HttpStatusCode.OK),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        using var client = new HttpClient(handler);
        await using var agent = new MemoryStream([1]);
        await using var exterior = new MemoryStream([2]);
        await using var kitchen = new MemoryStream([3]);
        var provider = CreateProvider(client);

        var submission = new NativePresenterVideoSubmission(
            new WalkthroughVideoFrame(agent, "image/jpeg"),
            [
                new(new WalkthroughVideoFrame(exterior, "image/jpeg"), "Exterior front", null, WalkthroughDirection.Ahead),
                new(new WalkthroughVideoFrame(kitchen, "image/jpeg"), "Kitchen", "A large center island."),
            ],
            "Welcome to your next home.",
            "Contact me today to schedule your private showing.",
            "Generate buyer interest.",
            "Upbeat instrumental with no lyrics.",
            "campaign-native-1");
        var result = await provider.SubmitNativePresenterAsync(submission);

        Assert.Equal("req_native_presenter", result.RequestId);
        // The recorded request is byte for byte the body that was sent, with the real uploaded image URLs.
        Assert.Equal(handler.Requests[^1].Body, result.Request.BodyJson);
        Assert.Equal("POST", result.Request.Method);
        Assert.Equal("https://api.higgsfield.test/kling-video/v4.0/image-to-video", result.Request.Url);
        Assert.DoesNotContain("secret", result.Request.BodyJson, StringComparison.OrdinalIgnoreCase);
        using var generation = JsonDocument.Parse(handler.Requests[^1].Body);
        var root = generation.RootElement;
        Assert.Equal("https://cdn.storage.test/frame-1.jpg", root.GetProperty("image_url").GetString());
        Assert.Equal(2, root.GetProperty("image_urls").GetArrayLength());
        Assert.Equal("https://cdn.storage.test/frame-2.jpg", root.GetProperty("image_urls")[0].GetString());
        Assert.Equal("https://cdn.storage.test/frame-3.jpg", root.GetProperty("image_urls")[1].GetString());
        Assert.Equal("https://cdn.storage.test/frame-1.jpg", root.GetProperty("end_image_url").GetString());
        Assert.Equal(30, root.GetProperty("duration").GetInt32());
        Assert.Equal("1080p", root.GetProperty("resolution").GetString());
        // The renderer uses Kling's own sound as the whole soundtrack, so it must be asked for explicitly.
        Assert.Equal("on", root.GetProperty("sound").GetString());
        Assert.Equal("9:16", root.GetProperty("aspect_ratio").GetString());
        var prompt = root.GetProperty("prompt").GetString()!;
        Assert.Equal(provider.BuildNativePresenterPrompt(submission), prompt);
        Assert.Contains("same real-estate agent shown in the first and last keyframe", prompt, StringComparison.Ordinal);
        Assert.Contains("says exactly: \"Welcome to your next home.\"", prompt, StringComparison.Ordinal);
        Assert.Contains("A large center island.", prompt, StringComparison.Ordinal);
        Assert.Contains("Upbeat instrumental with no lyrics.", prompt, StringComparison.Ordinal);
        Assert.Contains("native-presenter", provider.NativePresenterGenerationVersion, StringComparison.Ordinal);
        Assert.Equal(30_000, provider.NativePresenterDurationMs);
        Assert.Contains("Length: 30 seconds.", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void NativePresenterRequestUsesTheVideosOwnResolutionOrTheConfiguredOne()
    {
        using var client = new HttpClient(new RoutingHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        var provider = CreateProvider(client);
        NativePresenterVideoSubmission Submission(string? resolution) => new(
            new WalkthroughVideoFrame(Stream.Null, "image/jpeg"),
            [new(new WalkthroughVideoFrame(Stream.Null, "image/jpeg"), "Kitchen", null)],
            "Look at this kitchen.",
            "Save this for later.",
            "Reach.",
            "Music.",
            "campaign-native-pilot",
            "Bright and open.",
            10,
            resolution);

        var pilot = provider.BuildNativePresenterRequest(Submission("720p"), "agent", ["kitchen"]);
        var live = provider.BuildNativePresenterRequest(Submission(null), "agent", ["kitchen"]);

        using (var body = JsonDocument.Parse(pilot.BodyJson))
        {
            Assert.Equal("720p", body.RootElement.GetProperty("resolution").GetString());
            Assert.Equal(10, body.RootElement.GetProperty("duration").GetInt32());
            Assert.Equal("agent", body.RootElement.GetProperty("image_url").GetString());
            Assert.Equal("kitchen", body.RootElement.GetProperty("image_urls")[0].GetString());
        }

        using (var body = JsonDocument.Parse(live.BodyJson))
        {
            Assert.Equal("1080p", body.RootElement.GetProperty("resolution").GetString());
        }

        // Indented, one parameter per line, so the admin preview reads easily.
        Assert.Contains("\n  \"image_urls\": [", live.BodyJson.ReplaceLineEndings("\n"), StringComparison.Ordinal);

        Assert.Throws<ArgumentException>(() => provider.BuildNativePresenterRequest(Submission(null), "agent", []));
    }

    [Fact]
    public void NativePresenterPromptStatesEachVideosOwnLengthWithinKlingsLimit()
    {
        var submission = new NativePresenterVideoSubmission(
            new WalkthroughVideoFrame(Stream.Null, "image/jpeg"),
            [new(new WalkthroughVideoFrame(Stream.Null, "image/jpeg"), "Sunroom", null)],
            "You have to see this sunroom.",
            "Message me to see it.",
            "Maximize reach.",
            "Upbeat instrumental with no lyrics.",
            "campaign-native-wow",
            DurationSeconds: 15);

        Assert.Contains("Length: 15 seconds.", HiggsfieldWalkthroughVideoProvider.NativePresenterPrompt(submission), StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            HiggsfieldWalkthroughVideoProvider.NativePresenterPrompt(submission with { DurationSeconds = 45 }));
    }

    [Fact]
    public void NativePresenterPromptCarriesTheExactTourVoiceoverInsteadOfRoomHints()
    {
        var submission = new NativePresenterVideoSubmission(
            new WalkthroughVideoFrame(Stream.Null, "image/jpeg"),
            [
                new(new WalkthroughVideoFrame(Stream.Null, "image/jpeg"), "Exterior front", null, WalkthroughDirection.Ahead),
                new(new WalkthroughVideoFrame(Stream.Null, "image/jpeg"), "Kitchen", "A large center island."),
            ],
            "Hi, I'm Dana.",
            "Call me today.",
            "Generate buyer interest.",
            "Upbeat instrumental with no lyrics.",
            "campaign-native-2",
            "Step inside,  then the kitchen.");

        var prompt = HiggsfieldWalkthroughVideoProvider.NativePresenterPrompt(submission);

        Assert.Contains("Over the tour, the agent's voice says exactly: \"Step inside, then the kitchen.\"", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("A large center island.", prompt, StringComparison.Ordinal);
        Assert.Contains("says nothing beyond the supplied opening, voiceover and closing", prompt, StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => HiggsfieldWalkthroughVideoProvider.NativePresenterPrompt(
            submission with { TourVoiceover = "She said \"wow\"." }));
    }

    [Fact]
    public async Task PollDownloadsTheFinishedClipWithoutSendingTheKeyToTheVideoHost()
    {
        var handler = new RoutingHandler(request => request.RequestUri!.Host switch
        {
            "api.higgsfield.test" => Json(new
            {
                status = "completed",
                request_id = "req_123",
                video = new { url = "https://cdn.storage.test/out/req_123.mp4" },
            }),
            "cdn.storage.test" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ClipBytes) },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        using var client = new HttpClient(handler);

        var result = await CreateProvider(client).PollAsync("req_123");

        Assert.Equal(WalkthroughVideoState.Completed, result.State);
        await using var content = Assert.IsType<MemoryStream>(result.Content);
        Assert.Equal(ClipBytes, content.ToArray());
        Assert.Equal("higgsfield", result.Provider);
        Assert.Equal("https://api.higgsfield.test/requests/req_123/status", handler.Requests[0].Uri.AbsoluteUri);
        Assert.Null(handler.Requests[1].Authorization);
    }

    [Theory]
    [InlineData("queued", WalkthroughVideoState.InProgress)]
    [InlineData("in_progress", WalkthroughVideoState.InProgress)]
    [InlineData("failed", WalkthroughVideoState.Failed)]
    [InlineData("nsfw", WalkthroughVideoState.Failed)]
    [InlineData("canceled", WalkthroughVideoState.Failed)]
    public async Task PollMapsProviderStatuses(string status, WalkthroughVideoState expected)
    {
        var handler = new RoutingHandler(_ => Json(new { status, request_id = "req_123" }));
        using var client = new HttpClient(handler);

        var result = await CreateProvider(client).PollAsync("req_123");

        Assert.Equal(expected, result.State);
        Assert.Null(result.Content);
        Assert.Equal(expected == WalkthroughVideoState.Failed, result.FailureReason is not null);
    }

    [Fact]
    public async Task PollRejectsARequestIdThatCouldChangeThePath()
    {
        var handler = new RoutingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<ArgumentException>(() => CreateProvider(client).PollAsync("../files"));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task PollRejectsACompletedClipWithoutAnHttpsUrl()
    {
        var handler = new RoutingHandler(_ => Json(new { status = "completed", video = new { url = "http://cdn.test/a.mp4" } }));
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<InvalidDataException>(() => CreateProvider(client).PollAsync("req_123"));
    }

    [Fact]
    public async Task DisabledProviderSendsNothing()
    {
        var handler = new RoutingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(handler);
        var provider = CreateProvider(client, enabled: false);
        await using var image = new MemoryStream([1]);

        Assert.False(provider.IsEnabled);
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.SubmitAsync(new WalkthroughVideoSubmission(
            new WalkthroughVideoFrame(image, "image/jpeg"),
            new WalkthroughVideoFrame(image, "image/jpeg"),
            "transition-1")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.PollAsync("req_123"));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void GenerationVersionChangesWithTheModel()
    {
        using var client = new HttpClient(new RoutingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        var standard = CreateProvider(client).GenerationVersion;
        var other = CreateProvider(client, model: "alibaba/wan-3.0-prime/image-to-video").GenerationVersion;

        Assert.StartsWith("higgsfield/walkthrough-v1/", standard, StringComparison.Ordinal);
        Assert.NotEqual(standard, other);
    }

    private static HiggsfieldWalkthroughVideoProvider CreateProvider(
        HttpClient client,
        bool enabled = true,
        string model = "kling-video/v3.0/std/image-to-video") => new(
        client,
        Options.Create(new HiggsfieldOptions
        {
            Enabled = enabled,
            ApiKey = "key-id",
            ApiSecret = "key-secret",
            BaseUrl = "https://api.higgsfield.test/",
            Model = model,
            ClipDurationSeconds = 5,
            GenerationVersion = "walkthrough-v1",
            RequestTimeoutSeconds = 30,
            MaxOutputMegabytes = 1,
        }));

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
    };

    private sealed record RecordedRequest(
        HttpMethod Method,
        Uri Uri,
        string? Authorization,
        string? ContentType,
        IReadOnlyDictionary<string, string> Headers,
        byte[] BodyBytes)
    {
        public string Body => Encoding.UTF8.GetString(BodyBytes);
    }

    private sealed class RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri!,
                request.Headers.Authorization?.ToString(),
                request.Content?.Headers.ContentType?.MediaType,
                request.Headers.ToDictionary(header => header.Key, header => string.Join(',', header.Value)),
                body));
            return route(request);
        }
    }
}
