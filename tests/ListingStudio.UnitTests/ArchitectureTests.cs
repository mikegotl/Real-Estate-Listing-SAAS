using ListingStudio.Domain;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class ArchitectureTests
{
    [Fact]
    public void DomainHasNoProjectDependencies()
    {
        var referencedProjects = typeof(AssemblyMarker).Assembly
            .GetReferencedAssemblies()
            .Where(reference => reference.Name?.StartsWith("ListingStudio.", StringComparison.Ordinal) == true);

        Assert.Empty(referencedProjects);
    }
}
