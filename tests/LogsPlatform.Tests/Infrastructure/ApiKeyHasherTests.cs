// tests/LogsPlatform.Tests/Infrastructure/ApiKeyHasherTests.cs
using LogsPlatform.Infrastructure;
using Xunit;

namespace LogsPlatform.Tests.Infrastructure;

public class ApiKeyHasherTests
{
    [Fact]
    public void Hash_SameInput_ProducesSameOutput()
    {
        var first = ApiKeyHasher.Hash("lgp_sameraw");
        var second = ApiKeyHasher.Hash("lgp_sameraw");
        Assert.Equal(first, second);
    }

    [Fact]
    public void Hash_DifferentInput_ProducesDifferentOutput()
    {
        var first = ApiKeyHasher.Hash("lgp_raw1");
        var second = ApiKeyHasher.Hash("lgp_raw2");
        Assert.NotEqual(first, second);
    }
}
