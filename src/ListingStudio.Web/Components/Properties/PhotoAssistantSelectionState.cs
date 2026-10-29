namespace ListingStudio.Web.Components.Properties;

/// <summary>Per-browser-circuit photo context shared by the Media tab and the global Ask AI window.</summary>
public sealed class PhotoAssistantSelectionState
{
    public PhotoAssistantSelection? Selected { get; private set; }

    public event Action? Changed;
    public event Action? PhotoFixQueued;

    public void Select(PhotoAssistantSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        Selected = selection;
        Changed?.Invoke();
    }

    public void Clear()
    {
        Selected = null;
        Changed?.Invoke();
    }

    public void NotifyPhotoFixQueued() => PhotoFixQueued?.Invoke();
}

public sealed record PhotoAssistantSelection(
    Guid PropertyId,
    Guid MediaId,
    string Filename,
    string ImageUrl);
