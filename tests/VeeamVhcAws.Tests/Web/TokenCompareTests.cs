using VeeamVhcAws.Web;
using Xunit;

namespace VeeamVhcAws.Tests.Web;

public class TokenCompareTests
{
    [Fact]
    public void DifferentLengthTokens_DoNotMatch_AndDigestsStay32Bytes()
    {
        var shortDigest = AuthMiddleware.TokenDigest("a");
        var longDigest = AuthMiddleware.TokenDigest(new string('b', 64));

        Assert.Equal(32, shortDigest.Length);
        Assert.Equal(32, longDigest.Length);
        Assert.False(AuthMiddleware.TokensEqual("a", new string('b', 64)));
        Assert.True(AuthMiddleware.TokensEqual("same-token", "same-token"));
        Assert.False(AuthMiddleware.TokensEqual("same-token", "same-tokem"));
    }
}
