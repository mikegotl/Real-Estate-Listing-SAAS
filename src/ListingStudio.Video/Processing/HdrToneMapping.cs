namespace ListingStudio.Video.Processing;

/// <summary>
/// Phones record HDR video by default (HLG on iPhone, HLG or PQ on Android), in the wide BT.2020 color space. Read
/// as ordinary video it looks flat, grey and dull, so HDR sources are converted to standard range first.
/// </summary>
public static class HdrToneMapping
{
    /// <summary>
    /// Linear light, BT.709 primaries, the Hable curve (keeps highlights such as windows from clipping), then back to
    /// standard 8-bit video.
    /// </summary>
    public const string ToStandardRange =
        "zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,"
        + "tonemap=tonemap=hable:desat=0,"
        + "zscale=t=bt709:m=bt709:r=tv,format=yuv420p";

    /// <summary>ffprobe's color_transfer for HLG and for PQ (HDR10, Dolby Vision base layer).</summary>
    public static bool IsHdrTransfer(string? colorTransfer) =>
        colorTransfer is "arib-std-b67" or "smpte2084";

    /// <summary>ffprobe arguments that print the first video stream's color transfer and nothing else.</summary>
    public static IReadOnlyList<string> BuildTransferProbeArguments(string path) =>
    [
        "-v", "error",
        "-select_streams", "v:0",
        "-show_entries", "stream=color_transfer",
        "-of", "default=noprint_wrappers=1:nokey=1",
        path,
    ];
}
