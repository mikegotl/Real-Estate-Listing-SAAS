namespace ListingStudio.Web;

/// <summary>
/// Whether the Worker recognizes rooms in photos and video frames. The Web host reads the same switch as the Worker
/// so it only offers room recognition, such as Find room photos, when something will run it.
/// </summary>
public sealed class MediaAnalysisOptions
{
    public const string SectionName = "MediaAnalysis";

    public bool Enabled { get; init; }
}
