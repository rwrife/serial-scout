namespace SerialScout.Core.Tests;

public sealed class ProductInfoTests
{
    [Fact]
    public void ProductIdentityDescribesLocalFirstApplication()
    {
        Assert.Equal("Serial Scout", ProductInfo.DisplayName);
        Assert.True(ProductInfo.IsLocalFirst);
    }
}
