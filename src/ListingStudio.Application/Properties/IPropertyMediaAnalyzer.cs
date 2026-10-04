using ListingStudio.Domain.Properties;

namespace ListingStudio.Application.Properties;

public interface IPropertyMediaAnalyzer
{
    Task<PropertyMediaAnalysis> AnalyzeAsync(
        PropertyMediaAnalysisInput input,
        CancellationToken cancellationToken = default);
}

public sealed record PropertyMediaAnalysisInput(
    Guid MediaId,
    string OriginalFilename,
    string MimeType,
    Stream Content);
