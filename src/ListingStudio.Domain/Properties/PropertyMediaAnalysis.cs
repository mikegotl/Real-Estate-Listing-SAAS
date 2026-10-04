namespace ListingStudio.Domain.Properties;

public sealed record PropertyMediaAnalysis(
    PropertyMediaCategory Category,
    string RoomType,
    int QualityScore,
    int HeroScore,
    bool IsExterior,
    bool IsInterior,
    bool ContainsPeople,
    IReadOnlyList<string> PotentialProblems,
    string Description,
    int SuggestedDisplayOrder);
