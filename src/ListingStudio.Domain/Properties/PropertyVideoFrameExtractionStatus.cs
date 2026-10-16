namespace ListingStudio.Domain.Properties;

public enum PropertyVideoFrameExtractionStatus
{
    NotRequested,
    Pending,
    Extracting,
    Completed,
    Failed,
}

/// <summary>Where a running room-photo extraction is, shown to the agent as progress.</summary>
public enum PropertyVideoFrameExtractionStage
{
    FindingScenes,
    RecognizingRooms,
    AddingPhotos,
}
