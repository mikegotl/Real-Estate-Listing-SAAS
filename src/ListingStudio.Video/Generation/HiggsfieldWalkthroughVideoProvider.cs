using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ListingStudio.Application.Videos;
using ListingStudio.Application.Properties;
using ListingStudio.Domain.Campaigns;
using ListingStudio.Domain.Properties;
using ListingStudio.Video.Configuration;
using Microsoft.Extensions.Options;

namespace ListingStudio.Video.Generation;

/// <summary>
/// Higgsfield image-to-video with a first and last frame. Each listing photo is uploaded through a pre-signed URL,
/// the generation request is queued, and the status endpoint is polled until the MP4 is ready.
/// </summary>
public sealed partial class HiggsfieldWalkthroughVideoProvider(
    HttpClient httpClient,
    IOptions<HiggsfieldOptions> options) : IWalkthroughVideoProvider
{
    public const string ProviderName = "higgsfield";
    public const decimal AerialIntroPromptAdherence = 0.85m;
    public const int LowAerialIntroDurationSeconds = 8;
    public const int HighAerialIntroDurationSeconds = 12;

    public const string Prompt = """
        A smooth, steady first-person walk-through of a real home for a real-estate listing video.
        The camera starts on the first frame and moves naturally, at walking pace, to arrive exactly on the last frame,
        as if a buyer were walking from the first space into the next one.
        Preserve the architecture, rooms, furniture, windows, doors, fixtures, finishes and landscaping exactly as shown
        in the two photographs. Do not add, remove or redesign anything.
        Realistic real-estate photography. No people. No text. No camera shake.
        """;

    /// <summary>
    /// The prompt for one clip. Without a direction the model infers the path from the two frames and can turn the
    /// wrong way, so a direction set on the walkthrough map is spelled out from the buyer's point of view.
    /// </summary>
    public static string PromptFor(WalkthroughDirection? direction) => direction switch
    {
        null => Prompt,
        WalkthroughDirection.Ahead => Prompt + DirectionLine(
            "straight ahead, through the opening in front of the camera. Keep moving forward; do not turn left or right."),
        WalkthroughDirection.Left => Prompt + DirectionLine(
            "to the LEFT. The camera turns left (pans toward the left side of the first frame) while walking forward; "
            + "it must not turn right."),
        WalkthroughDirection.Right => Prompt + DirectionLine(
            "to the RIGHT. The camera turns right (pans toward the right side of the first frame) while walking forward; "
            + "it must not turn left."),
        WalkthroughDirection.Upstairs => Prompt + DirectionLine(
            "UPSTAIRS. The camera climbs the stairs, moving upward, to reach the upper floor."),
        WalkthroughDirection.Downstairs => Prompt + DirectionLine(
            "DOWNSTAIRS. The camera descends the stairs, moving downward, to reach the lower floor."),
        _ => throw new ArgumentOutOfRangeException(nameof(direction)),
    };

    /// <summary>
    /// The drone intro prompt. The clip starts on the real Exterior front photo and climbs away from it; the Worker
    /// plays it backwards, so the buyer sees a descent that lands on the photo. Keeping the camera on the facade and
    /// naming what not to invent limits how much of the roof, yard and street the model makes up.
    /// </summary>
    public static string AerialIntroPrompt(int heightFeet)
    {
        var finalComposition = heightFeet >= AerialIntroClip.HighHeightFeet
            ? "Finish in a clearly high aerial view with the entire roof, the whole visible lot, the street and nearby surroundings in frame. The house should be much smaller than it was in the first frame."
            : "Finish clearly above the roofline with the roof, front yard and street visible. The house should be noticeably smaller than it was in the first frame.";

        return $"""
        Real-estate aerial drone shot with one continuous, large-scale crane movement. The camera starts exactly on
        this ground-level photograph of the front of the house, then physically flies upward and backward over the
        front yard toward the street until it reaches an approximately {heightFeet}-foot aerial viewpoint.
        {finalComposition}
        Make the vertical travel unmistakable through a substantial change in altitude, perspective and subject size.
        Do not merely hover, drift a few feet, use a digital zoom, or stop at eave height. Keep the house centered while
        progressively tilting the camera downward as it climbs.
        Keep the facade, windows, doors, garage, colors, driveway and landscaping exactly as in the photograph.
        Do not add a swimming pool, water, a view, solar panels, extra buildings, people, moving cars or text.
        Steady, smooth, realistic drone footage. No camera shake.
        """;
    }

    public static int AerialIntroDurationSeconds(int heightFeet) =>
        heightFeet >= AerialIntroClip.HighHeightFeet
            ? HighAerialIntroDurationSeconds
            : LowAerialIntroDurationSeconds;

    /// <summary>One prompt for the ordered-keyframe tour; the per-stop path is appended by <see cref="FullTourPrompt"/>.</summary>
    public const string FullTourPromptPreamble = """
        Create one continuous, photorealistic first-person real-estate tour through the supplied keyframes in their
        exact order. Treat each image as a required room or exterior checkpoint, not as a loose style reference.
        Move at a comfortable walking pace and arrive recognizably at every supplied frame. Preserve the real home's
        architecture, room layout, doors, windows, fixtures, furniture, finishes and landscaping exactly as shown.
        Never invent connecting rooms, change the floor plan, add amenities, remove objects, or introduce people or
        text. Use smooth stabilized camera motion with no cuts, teleporting, camera shake, or impossible turns.
        """;

    public static string FullTourPrompt(IReadOnlyList<FullTourVideoFrame> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (frames.Count is < FullTourVideoPolicy.MinimumFrames or > FullTourVideoPolicy.MaximumFrames)
        {
            throw new ArgumentOutOfRangeException(nameof(frames), "A full tour needs two to ten ordered frames.");
        }

        static string SafeStop(string value)
        {
            var normalized = Regex.Replace(value.Trim(), "[^A-Za-z0-9 .'-]", string.Empty);
            return string.IsNullOrWhiteSpace(normalized) ? "tour stop" : normalized[..Math.Min(60, normalized.Length)];
        }

        var path = frames.Select((frame, index) =>
        {
            var stop = SafeStop(frame.StopName);
            if (index == frames.Count - 1)
            {
                return $"Keyframe {index + 1}: arrive in {stop}.";
            }

            var direction = frame.DirectionToNext is { } value
                ? WalkthroughMap.Describe(value)
                : "follow the visible architecture naturally";
            return $"Keyframe {index + 1}: {stop}; next, {direction}.";
        });
        return FullTourPromptPreamble + "\nRequired route:\n" + string.Join('\n', path);
    }

    public const string NativePresenterPromptPreamble = """
        Create one polished real-estate campaign video of the requested length with native synchronized speech and tasteful
        instrumental background music. The real-estate agent in the image_url photo is the on-camera presenter; that
        photo is only a reference for the agent's appearance, not a shot of its own. Preserve the agent's identity, face,
        skin tone, hair and clothing. The opening shot is the first tour keyframe with the agent placed into it, standing
        in front of the house and facing the camera while speaking the exact opening line. As the line ends, the agent
        gestures toward the home and the camera moves past the agent into the walk-through, continuing to the next tour
        keyframe. Then continue a brisk stabilized property tour through every remaining tour keyframe in its exact order.
        The agent appears on camera only in the opening shot; after it, the agent is heard but not seen, and the video
        ends inside the tour, not on the agent. Lower the music clearly under speech.
        Preserve the real property's architecture, layout, doors, windows, fixtures, finishes, furniture and landscaping.
        Do not invent rooms, amenities, views, people, claims, text, logos or contact information. Only mention the
        verified details supplied below. Do not show the agent inside a room.
        When an exact tour voiceover is supplied, the agent's voice speaks it word for word over the tour, and the agent
        says nothing beyond the supplied opening, voiceover and closing.
        """;

    public static string NativePresenterPrompt(NativePresenterVideoSubmission submission)
    {
        ArgumentNullException.ThrowIfNull(submission);
        if (submission.DurationSeconds is < NativePresenterVideoPolicy.MinimumDurationSeconds
            or > NativePresenterVideoPolicy.MaximumDurationSeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(submission), "Kling 4 makes 3 to 30 seconds of video per call.");
        }

        if (submission.TourFrames is null
            || submission.TourFrames.Count is < NativePresenterVideoPolicy.MinimumTourFrames
                or > NativePresenterVideoPolicy.MaximumTourFrames)
        {
            throw new ArgumentOutOfRangeException(
                nameof(submission),
                "A native-presenter tour needs one to eight approved Tour Map photos.");
        }

        static string Clean(string value, int maximumLength, string field)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value, field);
            var clean = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (clean.Length > maximumLength || clean.Any(char.IsControl))
            {
                throw new ArgumentOutOfRangeException(field);
            }

            return clean;
        }

        var exactVoiceover = string.IsNullOrWhiteSpace(submission.TourVoiceover)
            ? null
            : Clean(submission.TourVoiceover, NativePresenterScript.MaximumTourLength, nameof(submission.TourVoiceover));
        if (exactVoiceover?.Contains('"', StringComparison.Ordinal) == true)
        {
            throw new ArgumentOutOfRangeException(nameof(submission), "The tour voiceover cannot contain quotation marks.");
        }

        var route = submission.TourFrames.Select((frame, index) =>
        {
            var stop = Clean(frame.StopName, 80, nameof(frame.StopName));
            // With an exact voiceover the words are already fixed; a per-room hint would invite extra speech.
            var highlight = exactVoiceover is not null
                ? "Show the space; the voiceover covers it."
                : string.IsNullOrWhiteSpace(frame.ApprovedHighlight)
                    ? "Identify the space only; make no additional claim."
                    : $"Approved detail to mention briefly: {Clean(frame.ApprovedHighlight, 240, nameof(frame.ApprovedHighlight))}";
            var direction = index + 1 >= submission.TourFrames.Count
                ? "Hold on this space for the closing voiceover; do not cut back to the agent."
                : frame.DirectionToNext switch
                {
                    WalkthroughDirection.Ahead => "Continue straight ahead to the next keyframe.",
                    WalkthroughDirection.Left => "Turn left to reach the next keyframe.",
                    WalkthroughDirection.Right => "Turn right to reach the next keyframe.",
                    WalkthroughDirection.Upstairs => "Move upstairs to reach the next keyframe.",
                    WalkthroughDirection.Downstairs => "Move downstairs to reach the next keyframe.",
                    _ => "Move naturally to the next keyframe without inventing a connecting room.",
                };
            return $"Tour keyframe {index + 1}: {stop}. {highlight} {direction}";
        });

        return $"""
            {NativePresenterPromptPreamble}
            Length: {submission.DurationSeconds} seconds.
            Opening shot (first tour keyframe), the agent on camera in front of the house says exactly: "{Clean(submission.OpeningLine, 300, nameof(submission.OpeningLine))}"
            Campaign goal: {Clean(submission.CampaignGoal, 160, nameof(submission.CampaignGoal))}
            Music: {Clean(submission.MusicDirection, 160, nameof(submission.MusicDirection))}
            Pace: a confident introduction in front of the house, followed by a faster approximately 1.5x walking pace through the home.
            Required ordered tour:
            {string.Join('\n', route)}{(exactVoiceover is null ? string.Empty : $"\nOver the tour, the agent's voice says exactly: \"{exactVoiceover}\"")}
            Over the final tour shot, without appearing on camera, the agent's voice closes by saying exactly: "{Clean(submission.ClosingLine, 300, nameof(submission.ClosingLine))}"
            """;
    }

    private static string DirectionLine(string movement) =>
        $"\nDirection: the next space is {movement}";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public bool IsEnabled => options.Value.Enabled;

    public int ClipDurationMs => options.Value.ClipDurationSeconds * 1_000;

    public int FullTourDurationMs => options.Value.FullTourDurationSeconds * 1_000;

    public string GenerationVersion
    {
        get
        {
            var configuration = options.Value;
            var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{configuration.Model}\n{configuration.ClipDurationSeconds}\n{Prompt}")))[..12].ToLowerInvariant();
            return $"higgsfield/{configuration.GenerationVersion}/{identity}";
        }
    }

    public string AerialIntroGenerationVersion
    {
        get
        {
            var configuration = options.Value;
            var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{configuration.Model}\n{AerialIntroPromptAdherence}\n"
                + $"{LowAerialIntroDurationSeconds}\n{AerialIntroPrompt(AerialIntroClip.LowHeightFeet)}\n"
                + $"{HighAerialIntroDurationSeconds}\n{AerialIntroPrompt(AerialIntroClip.HighHeightFeet)}")))[..12].ToLowerInvariant();
            return $"higgsfield/{configuration.GenerationVersion}/aerial-{identity}";
        }
    }

    public string FullTourGenerationVersion
    {
        get
        {
            var configuration = options.Value;
            var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{configuration.FullTourModel}\n{configuration.FullTourDurationSeconds}\n"
                + $"{configuration.FullTourResolution}\n{FullTourPromptPreamble}")))[..12].ToLowerInvariant();
            return $"higgsfield/{configuration.GenerationVersion}/full-tour-{identity}";
        }
    }

    public string NativePresenterGenerationVersion
    {
        get
        {
            var configuration = options.Value;
            var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{configuration.FullTourModel}\n{configuration.FullTourDurationSeconds}\n"
                + $"{configuration.FullTourResolution}\n{NativePresenterPromptPreamble}\n"
                + $"{OptionalField(configuration.NativePresenterSound)}\n{OptionalField(configuration.NativePresenterAspectRatio)}")))[..12].ToLowerInvariant();
            return $"higgsfield/{configuration.GenerationVersion}/native-presenter-{identity}";
        }
    }

    public int NativePresenterDurationMs => options.Value.FullTourDurationSeconds * 1_000;

    public decimal EstimatedNativePresenterCostUsd => options.Value.EstimatedFullTourCostPerCallUsd;

    public async Task<string> SubmitAerialIntroAsync(
        AerialIntroVideoSubmission submission,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        if (submission.HeightFeet is < 20 or > 400)
        {
            throw new ArgumentOutOfRangeException(nameof(submission), "The drone height must be between 20 and 400 feet.");
        }

        var configuration = EnsureEnabled();
        var startUrl = await UploadAsync(configuration, submission.Frame, cancellationToken);
        return await QueueAsync(
            configuration,
            new GenerationRequest(
                AerialIntroPrompt(submission.HeightFeet),
                startUrl,
                null,
                AerialIntroDurationSeconds(submission.HeightFeet),
                "off",
                AerialIntroPromptAdherence),
            cancellationToken);
    }

    public async Task<string> SubmitAsync(
        WalkthroughVideoSubmission submission,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        var configuration = EnsureEnabled();
        var startUrl = await UploadAsync(configuration, submission.StartFrame, cancellationToken);
        var endUrl = await UploadAsync(configuration, submission.EndFrame, cancellationToken);
        return await QueueAsync(
            configuration,
            new GenerationRequest(
                PromptFor(submission.Direction),
                startUrl,
                endUrl,
                configuration.ClipDurationSeconds,
                "off",
                null),
            cancellationToken);
    }

    public async Task<string> SubmitFullTourAsync(
        FullTourVideoSubmission submission,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        if (submission.Frames is null
            || submission.Frames.Count is < FullTourVideoPolicy.MinimumFrames or > FullTourVideoPolicy.MaximumFrames)
        {
            throw new ArgumentOutOfRangeException(nameof(submission), "A full tour needs two to ten ordered frames.");
        }

        var configuration = EnsureEnabled();
        var urls = new List<string>(submission.Frames.Count);
        foreach (var frame in submission.Frames)
        {
            urls.Add(await UploadAsync(configuration, frame.Frame, cancellationToken));
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, ApiUri(configuration, configuration.FullTourModel.Trim('/')))
        {
            Content = JsonContent.Create(
                new FullTourGenerationRequest(
                    urls[0],
                    urls.Count > 2 ? urls.Skip(1).Take(urls.Count - 2).ToArray() : null,
                    urls[^1],
                    BuildFullTourPrompt(submission),
                    configuration.FullTourDurationSeconds,
                    configuration.FullTourResolution),
                options: SerializerOptions),
        };
        var queued = await SendForJsonAsync<QueuedResponse>(configuration, message, cancellationToken);
        return IsRequestId(queued?.RequestId)
            ? queued!.RequestId!
            : throw new InvalidDataException("Higgsfield did not return a valid request ID.");
    }

    public string BuildFullTourPrompt(FullTourVideoSubmission submission)
    {
        ArgumentNullException.ThrowIfNull(submission);
        return FullTourPrompt(submission.Frames);
    }

    public async Task<NativePresenterSubmissionResult> SubmitNativePresenterAsync(
        NativePresenterVideoSubmission submission,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        BuildNativePresenterPrompt(submission);
        var configuration = EnsureEnabled();
        var agentUrl = await UploadAsync(configuration, submission.AgentPortrait, cancellationToken);
        var tourUrls = new List<string>(submission.TourFrames.Count);
        foreach (var frame in submission.TourFrames)
        {
            tourUrls.Add(await UploadAsync(configuration, frame.Frame, cancellationToken));
        }

        // The body that is sent is the body that is recorded, so the audit shows exactly what Kling received.
        var request = BuildNativePresenterRequest(submission, agentUrl, tourUrls);
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(request.Url, UriKind.Absolute))
        {
            Content = new StringContent(request.BodyJson, System.Text.Encoding.UTF8, "application/json"),
        };
        var queued = await SendForJsonAsync<QueuedResponse>(configuration, message, cancellationToken);
        return IsRequestId(queued?.RequestId)
            ? new NativePresenterSubmissionResult(queued!.RequestId!, request)
            : throw new InvalidDataException("Higgsfield did not return a valid request ID.");
    }

    public ProviderRequestPreview BuildNativePresenterRequest(
        NativePresenterVideoSubmission submission,
        string agentImageUrl,
        IReadOnlyList<string> tourImageUrls)
    {
        ArgumentNullException.ThrowIfNull(submission);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentImageUrl);
        ArgumentNullException.ThrowIfNull(tourImageUrls);
        if (tourImageUrls.Count != submission.TourFrames.Count)
        {
            throw new ArgumentException("There must be one image URL for each Tour Map photo.", nameof(tourImageUrls));
        }

        var configuration = options.Value;
        var body = new FullTourGenerationRequest(
            agentImageUrl,
            tourImageUrls.ToArray(),
            null,
            BuildNativePresenterPrompt(submission),
            submission.DurationSeconds,
            OptionalField(submission.Resolution) ?? configuration.FullTourResolution,
            OptionalField(configuration.NativePresenterSound),
            OptionalField(configuration.NativePresenterAspectRatio));
        return new ProviderRequestPreview(
            "POST",
            ApiUri(configuration, configuration.FullTourModel.Trim('/')).ToString(),
            JsonSerializer.Serialize(body, IndentedSerializerOptions));
    }

    private static readonly JsonSerializerOptions IndentedSerializerOptions = new(SerializerOptions)
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string? OptionalField(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public string BuildNativePresenterPrompt(NativePresenterVideoSubmission submission)
    {
        ArgumentNullException.ThrowIfNull(submission);
        return NativePresenterPrompt(submission);
    }

    private async Task<string> QueueAsync(
        HiggsfieldOptions configuration,
        GenerationRequest request,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, ApiUri(configuration, configuration.Model.Trim('/')))
        {
            Content = JsonContent.Create(request, options: SerializerOptions),
        };
        var queued = await SendForJsonAsync<QueuedResponse>(configuration, message, cancellationToken);
        return IsRequestId(queued?.RequestId)
            ? queued!.RequestId!
            : throw new InvalidDataException("Higgsfield did not return a valid request ID.");
    }

    public async Task<WalkthroughVideoPollResult> PollAsync(
        string providerRequestId,
        CancellationToken cancellationToken = default)
    {
        var configuration = EnsureEnabled();
        if (!IsRequestId(providerRequestId))
        {
            throw new ArgumentException("The Higgsfield request ID is invalid.", nameof(providerRequestId));
        }

        using var message = new HttpRequestMessage(
            HttpMethod.Get,
            ApiUri(configuration, $"requests/{providerRequestId}/status"));
        var status = await SendForJsonAsync<StatusResponse>(configuration, message, cancellationToken)
            ?? throw new InvalidDataException("Higgsfield returned an empty status.");
        switch (status.Status?.ToLowerInvariant())
        {
            case "queued" or "in_progress" or "processing":
                return new WalkthroughVideoPollResult(
                    WalkthroughVideoState.InProgress, null, ProviderName, configuration.Model, null);
            case "completed":
                var videoUrl = status.Video?.Url;
                if (!Uri.TryCreate(videoUrl, UriKind.Absolute, out var videoUri) || videoUri.Scheme != Uri.UriSchemeHttps)
                {
                    throw new InvalidDataException("Higgsfield completed without an HTTPS video URL.");
                }

                var content = await DownloadAsync(configuration, videoUri, cancellationToken);
                return new WalkthroughVideoPollResult(
                    WalkthroughVideoState.Completed, content, ProviderName, configuration.Model, null);
            case "failed":
                return Failed(configuration, "Higgsfield could not generate this walk-through.");
            case "nsfw":
                return Failed(configuration, "Higgsfield's content filter rejected these photos.");
            case "canceled" or "cancelled":
                return Failed(configuration, "The Higgsfield request was canceled.");
            default:
                throw new InvalidDataException("Higgsfield returned an unknown request status.");
        }
    }

    private static WalkthroughVideoPollResult Failed(HiggsfieldOptions configuration, string reason) =>
        new(WalkthroughVideoState.Failed, null, ProviderName, configuration.Model, reason);

    private async Task<string> UploadAsync(
        HiggsfieldOptions configuration,
        WalkthroughVideoFrame frame,
        CancellationToken cancellationToken)
    {
        if (!frame.Content.CanRead || frame.ContentType is not ("image/jpeg" or "image/png" or "image/webp"))
        {
            throw new ArgumentException("Walk-through frames must be readable JPEG, PNG, or WEBP images.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, ApiUri(configuration, "files/generate-upload-url"))
        {
            Content = JsonContent.Create(new UploadUrlRequest(frame.ContentType), options: SerializerOptions),
        };
        var upload = await SendForJsonAsync<UploadUrlResponse>(configuration, request, cancellationToken);
        if (!Uri.TryCreate(upload?.UploadUrl, UriKind.Absolute, out var uploadUri)
            || uploadUri.Scheme != Uri.UriSchemeHttps
            || !Uri.TryCreate(upload.PublicUrl, UriKind.Absolute, out var publicUri)
            || publicUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidDataException("Higgsfield did not return HTTPS upload URLs.");
        }

        // The pre-signed URL carries its own authorization, so the API key is never sent to the storage host.
        using var put = new HttpRequestMessage(HttpMethod.Put, uploadUri) { Content = new StreamContent(frame.Content) };
        put.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(frame.ContentType);
        foreach (var (name, value) in upload.UploadHeaders ?? [])
        {
            if (!string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                put.Headers.TryAddWithoutValidation(name, value);
            }
        }

        using var response = await SendAsync(configuration, put, cancellationToken);
        EnsureSuccess(response, "upload a walk-through frame");
        return publicUri.AbsoluteUri;
    }

    private async Task<Stream> DownloadAsync(
        HiggsfieldOptions configuration,
        Uri videoUri,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, videoUri);
        using var response = await SendAsync(configuration, message, cancellationToken, bufferContent: false);
        EnsureSuccess(response, "download the walk-through clip");
        var maximumBytes = checked(configuration.MaxOutputMegabytes * 1024L * 1024L);
        if (response.Content.Headers.ContentLength > maximumBytes)
        {
            throw new InvalidDataException("The walk-through clip exceeds the configured size limit.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        var output = new MemoryStream();
        var buffer = new byte[81_920];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (output.Length + read > maximumBytes)
            {
                throw new InvalidDataException("The walk-through clip exceeds the configured size limit.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        if (output.Length == 0)
        {
            throw new InvalidDataException("Higgsfield returned an empty walk-through clip.");
        }

        output.Position = 0;
        return output;
    }

    private async Task<T?> SendForJsonAsync<T>(
        HiggsfieldOptions configuration,
        HttpRequestMessage message,
        CancellationToken cancellationToken)
    {
        message.Headers.Authorization = new AuthenticationHeaderValue(
            "Key",
            $"{configuration.ApiKey}:{configuration.ApiSecret}");
        using var response = await SendAsync(configuration, message, cancellationToken);
        EnsureSuccess(response, "reach the Higgsfield API");
        return await response.Content.ReadFromJsonAsync<T>(SerializerOptions, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HiggsfieldOptions configuration,
        HttpRequestMessage message,
        CancellationToken cancellationToken,
        bool bufferContent = true)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(configuration.RequestTimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            var response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            if (bufferContent)
            {
                await response.Content.LoadIntoBufferAsync(linked.Token);
            }

            return response;
        }
        catch (OperationCanceledException exception) when (
            timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The Higgsfield request timed out.", exception);
        }
    }

    private static void EnsureSuccess(HttpResponseMessage response, string action)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Could not {action}: Higgsfield returned HTTP {(int)response.StatusCode}.",
                null,
                response.StatusCode);
        }
    }

    private HiggsfieldOptions EnsureEnabled()
    {
        var configuration = options.Value;
        if (!configuration.Enabled)
        {
            throw new InvalidOperationException("Higgsfield walk-through generation is disabled.");
        }

        if (string.IsNullOrWhiteSpace(configuration.ApiKey)
            || string.IsNullOrWhiteSpace(configuration.ApiSecret)
            || string.IsNullOrWhiteSpace(configuration.Model)
            || !ModelPattern().IsMatch(configuration.Model)
            || configuration.Model.Contains("..", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(configuration.FullTourModel)
            || !ModelPattern().IsMatch(configuration.FullTourModel)
            || configuration.FullTourModel.Contains("..", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(configuration.GenerationVersion)
            || configuration.GenerationVersion.Length > 40
            || !Uri.TryCreate(configuration.BaseUrl, UriKind.Absolute, out var baseUrl)
            || baseUrl.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                "Enabled Higgsfield requires ApiKey, ApiSecret, a model endpoint, GenerationVersion, and an HTTPS BaseUrl.");
        }

        if (configuration.ClipDurationSeconds is < 3 or > 15
            || configuration.FullTourDurationSeconds is < 3 or > 30
            || configuration.FullTourResolution is not ("720p" or "1080p")
            || configuration.RequestTimeoutSeconds is < 10 or > 900
            || configuration.MaxOutputMegabytes is < 1 or > 500)
        {
            throw new InvalidOperationException("Higgsfield clip length, timeout, or output-size configuration is invalid.");
        }

        return configuration;
    }

    private static Uri ApiUri(HiggsfieldOptions configuration, string path) =>
        new($"{configuration.BaseUrl.TrimEnd('/')}/{path}", UriKind.Absolute);

    private static bool IsRequestId(string? value) => value is not null && RequestIdPattern().IsMatch(value);

    [GeneratedRegex("^[A-Za-z0-9_-]{1,200}$")]
    private static partial Regex RequestIdPattern();

    [GeneratedRegex(@"^[A-Za-z0-9._-]+(/[A-Za-z0-9._-]+)*$")]
    private static partial Regex ModelPattern();

    private sealed record UploadUrlRequest([property: JsonPropertyName("content_type")] string ContentType);

    private sealed record UploadUrlResponse(
        [property: JsonPropertyName("upload_url")] string? UploadUrl,
        [property: JsonPropertyName("public_url")] string? PublicUrl,
        [property: JsonPropertyName("upload_headers")] Dictionary<string, string>? UploadHeaders);

    private sealed record GenerationRequest(
        [property: JsonPropertyName("prompt")] string Prompt,
        [property: JsonPropertyName("image_url")] string ImageUrl,
        [property: JsonPropertyName("last_image_url"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LastImageUrl,
        [property: JsonPropertyName("duration")] int Duration,
        [property: JsonPropertyName("sound")] string Sound,
        [property: JsonPropertyName("cfg_scale"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? CfgScale);

    private sealed record FullTourGenerationRequest(
        [property: JsonPropertyName("image_url")] string ImageUrl,
        [property: JsonPropertyName("image_urls"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[]? ImageUrls,
        [property: JsonPropertyName("end_image_url"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EndImageUrl,
        [property: JsonPropertyName("prompt")] string Prompt,
        [property: JsonPropertyName("duration")] int Duration,
        [property: JsonPropertyName("resolution")] string Resolution,
        [property: JsonPropertyName("sound"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Sound = null,
        [property: JsonPropertyName("aspect_ratio"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AspectRatio = null);

    private sealed record QueuedResponse([property: JsonPropertyName("request_id")] string? RequestId);

    private sealed record StatusResponse(
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("video")] VideoResponse? Video);

    private sealed record VideoResponse([property: JsonPropertyName("url")] string? Url);
}
