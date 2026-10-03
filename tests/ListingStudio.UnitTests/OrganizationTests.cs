using ListingStudio.Domain.Organizations;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class OrganizationTests
{
    [Fact]
    public void CreateTrimsOrganizationName()
    {
        var organization = Organization.Create("  Acme Realty  ");

        Assert.Equal("Acme Realty", organization.Name);
        Assert.NotEqual(Guid.Empty, organization.Id);
    }

    [Fact]
    public void CreateRejectsMissingOrganizationName()
    {
        Assert.Throws<ArgumentException>(() => Organization.Create(" "));
    }

    [Fact]
    public void CreateOwnerAssignsTenantAndOwnerRole()
    {
        var organizationId = Guid.NewGuid();

        var member = OrganizationMember.CreateOwner(organizationId, "user-id");

        Assert.Equal(organizationId, member.OrganizationId);
        Assert.Equal("user-id", member.UserId);
        Assert.Equal(OrganizationMemberRole.Owner, member.Role);
    }
}
