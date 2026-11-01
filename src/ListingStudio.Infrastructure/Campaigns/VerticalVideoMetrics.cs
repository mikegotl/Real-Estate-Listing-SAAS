using System.Diagnostics.Metrics;
using ListingStudio.Domain.Campaigns;

namespace ListingStudio.Infrastructure.Campaigns;

/// <summary>
/// Operational metrics for the vertical video library: how many videos, how long each step took, and how much voice
/// text was synthesized (the voice providers bill by character). Tags are formats and outcomes only, never listing
/// content, workspace or property identities.
/// </summary>
public static class VerticalVideoMetrics
{
    public const string MeterName = "ListingStudio.VerticalVideoLibrary";

    private static readonly Meter Meter = new(MeterName, "1.0");

    private static readonly Counter<long> LibrariesPlanned = Meter.CreateCounter<long>(
        "vertical_library.planned", description: "Vertical video libraries planned.");

    private static readonly Histogram<int> LibrarySize = Meter.CreateHistogram<int>(
        "vertical_library.videos", unit: "{video}", description: "Videos planned per library.");

    private static readonly Counter<long> LibrariesFinished = Meter.CreateCounter<long>(
        "vertical_library.finished", description: "Vertical video libraries finished, by outcome.");

    private static readonly Histogram<double> StepDuration = Meter.CreateHistogram<double>(
        "vertical_library.step.duration", unit: "s", description: "Time to plan or render one library video.");

    private static readonly Counter<long> NarrationCharacters = Meter.CreateCounter<long>(
        "vertical_library.narration.characters", unit: "{character}", description: "Voiceover characters synthesized.");

    public static void RecordLibraryPlanned(int videos, TimeSpan elapsed)
    {
        LibrariesPlanned.Add(1);
        LibrarySize.Record(videos);
        StepDuration.Record(elapsed.TotalSeconds, new KeyValuePair<string, object?>("step", "recommend"));
    }

    public static void RecordLibraryFinished(CampaignVideoLibraryStatus status) =>
        LibrariesFinished.Add(1, new KeyValuePair<string, object?>("status", status.ToString()));

    public static void RecordStep(string step, VerticalCreativeFormat format, string outcome, TimeSpan elapsed) =>
        StepDuration.Record(
            elapsed.TotalSeconds,
            new KeyValuePair<string, object?>("step", step),
            new KeyValuePair<string, object?>("format", format.ToString()),
            new KeyValuePair<string, object?>("outcome", outcome));

    public static void RecordNarrationCharacters(VerticalCreativeFormat format, int characters) =>
        NarrationCharacters.Add(characters, new KeyValuePair<string, object?>("format", format.ToString()));
}
