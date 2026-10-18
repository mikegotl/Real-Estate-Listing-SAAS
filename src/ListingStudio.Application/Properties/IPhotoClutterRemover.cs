namespace ListingStudio.Application.Properties;

/// <summary>
/// An AI image edit that removes movable clutter from a listing photo and keeps every permanent feature of the home
/// as it is, so the photo still shows the property truthfully.
/// </summary>
public interface IPhotoClutterRemover
{
    /// <summary>False until the service is switched on and configured; the Media tab then hides Remove clutter.</summary>
    bool IsEnabled { get; }

    /// <summary>Returns the edited canvas as an encoded image, the same size as the input canvas.</summary>
    Task<byte[]> RemoveClutterAsync(PhotoClutterRemovalRequest request, CancellationToken cancellationToken = default);
}

/// <param name="Canvas">The photo padded onto the editor canvas, as a PNG.</param>
/// <param name="Mask">A PNG of the same size, transparent over the photo and opaque over the padding.</param>
public sealed record PhotoClutterRemovalRequest(
    byte[] Canvas,
    byte[] Mask,
    int Width,
    int Height);

/// <summary>
/// A photo fix that will fail the same way if tried again, such as an edit the AI service declines. Its message is
/// written for the agent and shown on the photo.
/// </summary>
public sealed class PhotoFixRejectedException(string message) : InvalidOperationException(message);
