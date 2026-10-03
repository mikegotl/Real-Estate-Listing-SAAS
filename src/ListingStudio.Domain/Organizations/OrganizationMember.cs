namespace ListingStudio.Domain.Organizations;

public sealed class OrganizationMember
{
    private OrganizationMember()
    {
    }

    private OrganizationMember(Guid organizationId, string userId, OrganizationMemberRole role)
    {
        Id = Guid.NewGuid();
        OrganizationId = organizationId;
        UserId = userId;
        Role = role;
        JoinedAtUtc = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public string UserId { get; private set; } = string.Empty;

    public OrganizationMemberRole Role { get; private set; }

    public DateTimeOffset JoinedAtUtc { get; private set; }

    public Organization Organization { get; private set; } = null!;

    public static OrganizationMember CreateOwner(Guid organizationId, string userId)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("An organization is required.", nameof(organizationId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        return new OrganizationMember(organizationId, userId, OrganizationMemberRole.Owner);
    }
}

public enum OrganizationMemberRole
{
    Owner = 1,
    Member = 2,
}
