namespace ListingStudio.Domain.Organizations;

using ListingStudio.Domain.Properties;
using ListingStudio.Domain.Stories;

public sealed class Organization
{
    private Organization()
    {
    }

    private Organization(string name)
    {
        Id = Guid.NewGuid();
        Name = name;
        CreatedAtUtc = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public ICollection<OrganizationMember> Members { get; } = [];

    public ICollection<ListingProperty> Properties { get; } = [];

    public ICollection<PropertyStory> PropertyStories { get; } = [];


    public static Organization Create(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var normalizedName = name.Trim();

        if (normalizedName.Length > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(name), "Organization names cannot exceed 200 characters.");
        }

        return new Organization(normalizedName);
    }
}
