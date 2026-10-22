using ListingStudio.Application.Administration;

namespace ListingStudio.Infrastructure.Administration;

public sealed class AiUsagePropertyContext : IAiUsagePropertyContext
{
    private readonly AsyncLocal<Scope?> current = new();

    public Guid? PropertyId => current.Value?.PropertyId;

    public IDisposable BeginProperty(Guid propertyId)
    {
        if (propertyId == Guid.Empty)
        {
            throw new ArgumentException("A property identity is required.", nameof(propertyId));
        }

        var prior = current.Value;
        var scope = new Scope(propertyId, prior, current);
        current.Value = scope;
        return scope;
    }

    private sealed class Scope(
        Guid propertyId,
        Scope? prior,
        AsyncLocal<Scope?> current) : IDisposable
    {
        private bool disposed;

        public Guid PropertyId { get; } = propertyId;

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            if (ReferenceEquals(current.Value, this))
            {
                current.Value = prior;
            }

            disposed = true;
        }
    }
}
