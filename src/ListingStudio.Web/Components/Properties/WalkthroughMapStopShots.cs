using ListingStudio.Application.Properties;

namespace ListingStudio.Web.Components.Properties;

/// <summary>A photographed storyboard stop and its shots in walkthrough order, as the walkthrough map draws it.</summary>
public sealed record WalkthroughMapStopShots(string Name, IReadOnlyList<PropertyMediaItem> Shots);
