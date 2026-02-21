using Xunit;

namespace Uplink2.Tests;

/// <summary>Minimal smoke test to validate the test project wiring.</summary>
public sealed class AlwaysPassTests
{
    /// <summary>Ensures baseline test execution is working.</summary>
    [Fact]
    public void AlwaysPasses()
    {
        Assert.True(true);
    }
}
