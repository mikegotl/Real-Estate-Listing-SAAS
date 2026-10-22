using ListingStudio.Infrastructure.Administration;
using Xunit;

namespace ListingStudio.UnitTests;

public sealed class AiUsagePropertyContextTests
{
    [Fact]
    public async Task PropertyAttributionFlowsAcrossAwaitAndRestoresNestedScopes()
    {
        var context = new AiUsagePropertyContext();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        Assert.Null(context.PropertyId);
        using (context.BeginProperty(first))
        {
            await Task.Yield();
            Assert.Equal(first, context.PropertyId);
            using (context.BeginProperty(second))
            {
                await Task.Yield();
                Assert.Equal(second, context.PropertyId);
            }

            Assert.Equal(first, context.PropertyId);
        }

        Assert.Null(context.PropertyId);
    }
}
