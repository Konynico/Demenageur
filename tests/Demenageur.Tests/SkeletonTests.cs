using Demenageur.Tests.Infrastructure;

namespace Demenageur.Tests;

public sealed class SkeletonTests
{
    [Fact]
    public void Tests_run_without_elevation()
    {
        Assert.False(NotElevatedFixture.IsElevated());
    }
}
