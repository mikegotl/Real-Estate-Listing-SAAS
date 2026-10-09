using System.Text.Json;
using ListingStudio.Application.Stories;
using ListingStudio.Application.Videos;
using ListingStudio.Domain.Properties;
using ListingStudio.Domain.Videos;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class VideoProductionSpecificationValidatorTests
{
    private readonly VideoProductionSpecificationValidator validator = new();

    [Theory]
    [InlineData(RequestedDuration.Teaser15, VideoAspectRatio.Landscape16By9)]
    [InlineData(RequestedDuration.Teaser15, VideoAspectRatio.Vertical9By16)]
    [InlineData(RequestedDuration.Feature30, VideoAspectRatio.Landscape16By9)]
    [InlineData(RequestedDuration.Feature30, VideoAspectRatio.Vertical9By16)]
    [InlineData(RequestedDuration.Hero60, VideoAspectRatio.Landscape16By9)]
    [InlineData(RequestedDuration.Hero60, VideoAspectRatio.Vertical9By16)]
    public void AcceptsSupportedDurationsAndAspectRatios(
        RequestedDuration duration,
        VideoAspectRatio aspectRatio)
    {
        var input = CreateInput(duration, aspectRatio);

        var result = validator.Validate(input, CreateSpecification(input));

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
    }

    [Fact]
    public void RejectsNonContiguousTimelineAndIncorrectTotalDuration()
    {
        var input = CreateInput();
        var specification = CreateSpecification(input);
        var first = specification.Scenes[0];
        specification = specification with
        {
            Scenes =
            [
                first with { DurationMs = first.DurationMs - 1 },
                first with
                {
                    SceneNumber = 2,
                    StartMs = first.DurationMs,
                    DurationMs = 1,
                    TextOverlays = [],
                    NarrationSegmentIds = [],
                    TransitionIn = new TransitionPlan(TransitionKind.Crossfade, 1),
                },
            ],
        };

        var result = validator.Validate(input, specification);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("contiguous", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsUnknownMediaAndAlteredGroundedText()
    {
        var input = CreateInput();
        var specification = CreateSpecification(input);
        var scene = specification.Scenes[0];
        specification = specification with
        {
            Scenes =
            [
                scene with
                {
                    VisualSource = scene.VisualSource with { PropertyMediaId = Guid.NewGuid() },
                    TextOverlays =
                    [
                        scene.TextOverlays[0] with { Text = "Invented ocean views" },
                    ],
                },
            ],
        };

        var result = validator.Validate(input, specification);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("outside the authoritative set", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("fact binding", StringComparison.Ordinal));
    }

    [Fact]
    public void RequiresEveryApprovedNeighborhoodFactToBeDisplayedOrNarrated()
    {
        var original = CreateInput();
        var neighborhood = new FactBinding(
            "neighborhood.1",
            "Example Park • Park • 0.5 miles straight-line distance",
            FactSource.ApprovedNeighborhood,
            "https://maps.google.test/example-park");
        var input = original with { FactBindings = [.. original.FactBindings, neighborhood] };
        var specification = CreateSpecification(input);

        var missing = validator.Validate(input, specification);

        Assert.False(missing.IsValid);
        Assert.Contains(missing.Errors, error => error.Contains(
            "Approved neighborhood fact neighborhood.1",
            StringComparison.Ordinal));

        var scene = specification.Scenes[0];
        specification = specification with
        {
            Scenes =
            [
                scene with
                {
                    TextOverlays =
                    [
                        .. scene.TextOverlays,
                        new TextOverlay(
                            "neighborhood-1",
                            neighborhood.Value,
                            neighborhood.Key,
                            5_000,
                            3_000,
                            OverlayAnchor.BottomCenter,
                            new NormalizedRect(0.15m, 0.60m, 0.70m, 0.10m),
                            TextOverlayStyle.PropertyFact),
                    ],
                },
            ],
        };

        var included = validator.Validate(input, specification);

        Assert.True(included.IsValid, string.Join(Environment.NewLine, included.Errors));
    }

    [Fact]
    public void RejectsUnsafeLayoutNarrationMismatchAndUnknownAssets()
    {
        var input = CreateInput();
        var specification = CreateSpecification(input);
        var scene = specification.Scenes[0];
        specification = specification with
        {
            Audio = specification.Audio with
            {
                Music = new MusicPlan("unapproved", MusicMood.WarmCinematic, 0, 60_000, -18, 1_000, 1_000, -28),
            },
            Scenes =
            [
                scene with
                {
                    TextOverlays =
                    [
                        scene.TextOverlays[0] with { Box = new NormalizedRect(0, 0, 1, 1) },
                    ],
                    NarrationSegmentIds = [],
                },
            ],
        };

        var result = validator.Validate(input, specification);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("safe zone", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("narration references", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("Music asset", StringComparison.Ordinal));
    }

    [Fact]
    public void RequiresApprovedGeneratedClipAndPropertyPhotoFallback()
    {
        var input = CreateInput();
        var specification = CreateSpecification(input);
        var scene = specification.Scenes[0];
        specification = specification with
        {
            Scenes =
            [
                scene with
                {
                    VisualSource = new VisualSource(
                        VisualSourceKind.GeneratedClip,
                        null,
                        Guid.NewGuid(),
                        null,
                        null),
                },
            ],
        };

        var result = validator.Validate(input, specification);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("invalid generatedClip", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("unapproved generated clip", StringComparison.Ordinal));
    }

    [Fact]
    public void AcceptsGenerativeMotionRequestWithAuthoritativeFallback()
    {
        var input = CreateInput();
        var specification = CreateSpecification(input);
        var scene = specification.Scenes[0];
        specification = specification with
        {
            Scenes =
            [
                scene with
                {
                    VisualSource = new VisualSource(
                        VisualSourceKind.GenerativeMotionRequest,
                        input.Media[0].MediaId,
                        null,
                        input.Media[0].MediaId,
                        "Slow camera push while preserving the property image."),
                },
            ],
        };

        var result = validator.Validate(input, specification);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
    }

    [Fact]
    public void RejectsDuplicateAuthoritativeMediaIds()
    {
        var input = CreateInput();
        input = input with { Media = [input.Media[0], input.Media[0]] };

        var result = validator.Validate(input, CreateSpecification(input));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("duplicate IDs", StringComparison.Ordinal));
    }

    [Fact]
    public void AcceptedScriptMustBeNarratedVerbatimInOrderAndCanUseApprovedWalkthroughVideo()
    {
        var original = CreateInput();
        var videoId = Guid.NewGuid();
        var script = new AcceptedNarrationScriptInput(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            [
                new AcceptedNarrationScriptSegment("acceptedScript.segment.1", "Begin in the sunlit living room."),
                new AcceptedNarrationScriptSegment("acceptedScript.segment.2", "Continue through the open kitchen."),
            ]);
        var scriptBindings = script.Segments.Select(segment =>
            new FactBinding(segment.Key, segment.Text, FactSource.AcceptedScript, script.ScriptId.ToString("D")));
        var input = original with
        {
            FactBindings = [.. original.FactBindings, .. scriptBindings],
            AcceptedNarrationScript = script,
            WalkthroughVideos = [new VideoWalkthroughInput(videoId, "walkthrough.mp4", 1_920, 1_080, 90_000)],
        };
        var specification = CreateSpecification(input);
        var acceptedNarration = new[]
        {
            new NarrationSegment("script-1", 500, 3_000, script.Segments[0].Text, script.Segments[0].Key),
            new NarrationSegment("script-2", 4_000, 3_000, script.Segments[1].Text, script.Segments[1].Key),
        };
        var scene = specification.Scenes[0] with
        {
            VisualSource = new VisualSource(
                VisualSourceKind.PropertyVideo, null, null, null, null, videoId, 10_000),
            NarrationSegmentIds = acceptedNarration.Select(segment => segment.Id).ToArray(),
        };
        specification = specification with
        {
            Audio = specification.Audio with { NarrationSegments = acceptedNarration },
            Scenes = [scene],
        };

        var valid = validator.Validate(input, specification);
        Assert.True(valid.IsValid, string.Join(Environment.NewLine, valid.Errors));

        var altered = specification with
        {
            Audio = specification.Audio with
            {
                NarrationSegments =
                [
                    acceptedNarration[0],
                    acceptedNarration[1] with { Text = "A paraphrased kitchen description." },
                ],
            },
        };
        var invalid = validator.Validate(input, altered);
        Assert.False(invalid.IsValid);
        Assert.Contains(invalid.Errors, error => error.Contains("preserve the accepted script", StringComparison.Ordinal));
    }

    [Fact]
    public void SerializesCanonicalDurationAndAspectRatio()
    {
        var input = CreateInput(RequestedDuration.Hero60, VideoAspectRatio.Landscape16By9);

        using var document = JsonDocument.Parse(
            JsonSerializer.Serialize(CreateSpecification(input), VideoSpecificationJson.Options));

        Assert.Equal(60, document.RootElement.GetProperty("requestedDurationSeconds").GetInt32());
        Assert.Equal("16:9", document.RootElement.GetProperty("aspectRatio").GetString());
        Assert.Equal("propertyMedia", document.RootElement
            .GetProperty("scenes")[0]
            .GetProperty("visualSource")
            .GetProperty("kind")
            .GetString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void RejectsSchemaNullsUnsupportedEnumsAndOutOfRangeGain(int mutation)
    {
        var input = CreateInput();
        var plan = CreateSpecification(input);
        plan = mutation switch
        {
            0 => plan with { Audio = plan.Audio with { Music = plan.Audio.Music with { GainDb = 10 } } },
            1 => plan with { Scenes = [plan.Scenes[0] with { Motion = plan.Scenes[0].Motion with { Easing = (MotionEasing)999 } }] },
            2 => plan with { Scenes = null! },
            3 => plan with { Audio = null! },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        Assert.False(validator.Validate(input, plan).IsValid);
    }

    [Fact]
    public void RejectsFeatureChangingGenerativeInstructions()
    {
        var input = CreateInput();
        var plan = CreateSpecification(input);
        var scene = plan.Scenes[0];
        plan = plan with
        {
            Scenes = [scene with { VisualSource = new(VisualSourceKind.GenerativeMotionRequest,
            input.Media[0].MediaId, null, input.Media[0].MediaId, "Add an ocean view and a pool") }]
        };
        Assert.False(validator.Validate(input, plan).IsValid);
    }

    [Fact]
    public void CanonicalApprovedSampleRoundTripsWithoutSchemaDrift()
    {
        using var stream = typeof(VideoProductionSpecificationValidatorTests).Assembly.GetManifestResourceStream("CanonicalVideoSample")!;
        using var source = JsonDocument.Parse(stream);
        var plan = source.RootElement.Deserialize<VideoProductionSpecification>(VideoSpecificationJson.Options)!;
        var roundTrip = JsonSerializer.SerializeToElement(plan, VideoSpecificationJson.Options);
        Assert.True(JsonElement.DeepEquals(source.RootElement, roundTrip));
        Assert.Empty(VideoSchemaContract.Validate(roundTrip));
    }

    [Fact]
    public void FiniteVocabulariesExactlyMatchApprovedSchema()
    {
        using var stream = typeof(VideoSchemaContract).Assembly.GetManifestResourceStream("VideoProductionSchema")!;
        using var schema = JsonDocument.Parse(stream);
        var definitions = schema.RootElement.GetProperty("$defs");
        var moods = definitions.GetProperty("musicPlan").GetProperty("properties").GetProperty("mood").GetProperty("enum")
            .EnumerateArray().Select(value => value.GetString()).Order(StringComparer.Ordinal);
        var anchors = definitions.GetProperty("textOverlay").GetProperty("properties").GetProperty("anchor").GetProperty("enum")
            .EnumerateArray().Select(value => value.GetString()).Order(StringComparer.Ordinal);
        Assert.Equal(moods, Enum.GetValues<MusicMood>().Select(value => JsonSerializer.SerializeToElement(value, VideoSpecificationJson.Options).GetString()).Order(StringComparer.Ordinal));
        Assert.Equal(anchors, Enum.GetValues<OverlayAnchor>().Select(value => JsonSerializer.SerializeToElement(value, VideoSpecificationJson.Options).GetString()).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void StructuredOutputSchemasRequireEveryDeclaredObjectProperty()
    {
        using var stream = typeof(VideoSchemaContract).Assembly.GetManifestResourceStream("VideoProductionSchema")!;
        using var schema = JsonDocument.Parse(stream);

        AssertAllObjectPropertiesRequired(schema.RootElement, "$canonical");
        AssertAllObjectPropertiesRequired(VideoSchemaContract.EditorialSchema, "$editorial");
    }

    private static void AssertAllObjectPropertiesRequired(JsonElement node, string path)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            if (node.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String
                && type.GetString() == "object"
                && node.TryGetProperty("properties", out var properties))
            {
                var required = node.TryGetProperty("required", out var requiredNode)
                    ? requiredNode.EnumerateArray()
                        .Select(value => value.GetString()!)
                        .ToHashSet(StringComparer.Ordinal)
                    : [];

                foreach (var property in properties.EnumerateObject())
                {
                    Assert.True(
                        required.Contains(property.Name),
                        $"{path}.properties.{property.Name} must be listed in required for strict Structured Outputs.");
                }
            }

            foreach (var property in node.EnumerateObject())
            {
                AssertAllObjectPropertiesRequired(property.Value, $"{path}.{property.Name}");
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in node.EnumerateArray())
            {
                AssertAllObjectPropertiesRequired(item, $"{path}[{index++}]");
            }
        }
    }

    private static VideoDirectionRequest CreateInput(
        RequestedDuration duration = RequestedDuration.Hero60,
        VideoAspectRatio aspectRatio = VideoAspectRatio.Landscape16By9)
    {
        var mediaId = Guid.NewGuid();
        var landscape = aspectRatio == VideoAspectRatio.Landscape16By9;
        var output = landscape
            ? new VideoOutputProfile(1920, 1080, 30, "h264", "aac", "yuv420p", 48_000, 2)
            : new VideoOutputProfile(1080, 1920, 30, "h264", "aac", "yuv420p", 48_000, 2);
        var safeZone = landscape
            ? new NormalizedRect(0.05m, 0.05m, 0.90m, 0.90m)
            : new NormalizedRect(0.075m, 0.05m, 0.85m, 0.90m);
        var bindings = new[]
        {
            new FactBinding("story.voiceover", "Welcome home.", FactSource.PropertyStory, "VoiceoverScript"),
            new FactBinding("story.closingCta", "Contact the listing team.", FactSource.PropertyStory, "ClosingCta"),
        };
        return new VideoDirectionRequest(
            Guid.NewGuid(),
            new VideoPropertyStoryInput(
                Guid.NewGuid(),
                1,
                new PropertyStoryContentSnapshot("A welcome", "Welcome.", "A home.", [], "Welcome home.", "Contact the listing team.")),
            new VerifiedPropertyData(
                "123 Main Street", null, "Raleigh", "NC", "27601", 450_000m, 3, 2.5m, 2_100, null,
                1998, PropertyType.SingleFamily, "A home.", ListingStatus.Active),
            [
                new VideoMediaInput(
                    mediaId,
                    landscape ? 1_600 : 900,
                    landscape ? 900 : 1_600,
                    new PropertyMediaObservation(
                        mediaId, 0, PropertyMediaCategory.FrontExterior, "Exterior", 90, 95, true, false,
                        false, [], "Front exterior.", 0)),
            ],
            duration,
            aspectRatio,
            output,
            safeZone,
            bindings,
            new BrandKit(null, null, null, null, null, null, "#17324D", "#F4F0E8"),
            new GroundedText("Contact the listing team.", "story.closingCta"),
            new HashSet<Guid>(),
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal));
    }

    private static VideoProductionSpecification CreateSpecification(VideoDirectionRequest input)
    {
        var programDuration = (int)input.RequestedDuration * 1_000;
        var narration = new NarrationSegment("narration-1", 500, 3_000, "Welcome home.", "story.voiceover");
        var viewport = new NormalizedRect(0, 0, 1, 1);
        var scene = new VideoScene(
            1,
            0,
            programDuration,
            new VisualSource(VisualSourceKind.PropertyMedia, input.Media[0].MediaId, null, null, null),
            new TransitionPlan(TransitionKind.Cut, 0),
            new MotionPlan(MotionKind.None, viewport, viewport, MotionEasing.Linear),
            [
                new TextOverlay(
                    "cta", input.CallToAction.Text, input.CallToAction.GroundingKey,
                    programDuration - 4_000, 3_000, OverlayAnchor.BottomCenter,
                    new NormalizedRect(input.SafeZone.X + 0.05m, 0.75m, input.SafeZone.Width - 0.10m, 0.10m),
                    TextOverlayStyle.ClosingCta),
            ],
            [],
            [narration.Id]);
        return new VideoProductionSpecification(
            "1.0",
            input.PropertyId,
            input.PropertyStory.Id,
            input.PropertyStory.Version,
            input.RequestedDuration,
            input.AspectRatio,
            input.Output,
            input.SafeZone,
            input.FactBindings,
            input.Brand,
            input.CallToAction,
            new AudioPlan(
                [narration],
                new MusicPlan(null, MusicMood.None, 0, 0, 0, 0, 0, 0)),
            [scene]);
    }
}
