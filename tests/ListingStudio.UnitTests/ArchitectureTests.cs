using ListingStudio.Domain;

namespace ListingStudio.UnitTests;

public sealed class ArchitectureTests
{
    [Fact]
    public void Domain_has_no_project_dependencies()
    {
        var referencedProjects = typeof(AssemblyMarker).Assembly
            .GetReferencedAssemblies()
            .Where(reference => reference.Name?.StartsWith("ListingStudio.", StringComparison.Ordinal) == true);

        Assert.Empty(referencedProjects);
    }
}
