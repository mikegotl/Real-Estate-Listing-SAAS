namespace ListingStudio.Domain.Administration;

public sealed class PlatformMetricsReset
{
    private PlatformMetricsReset()
    {
    }

    private PlatformMetricsReset(DateTimeOffset resetAtUtc)
    {
        Id = Guid.NewGuid();
        ResetAtUtc = resetAtUtc;
    }

    public Guid Id { get; private set; }
    public DateTimeOffset ResetAtUtc { get; private set; }

    public static PlatformMetricsReset Create(DateTimeOffset resetAtUtc) => new(resetAtUtc);
}
