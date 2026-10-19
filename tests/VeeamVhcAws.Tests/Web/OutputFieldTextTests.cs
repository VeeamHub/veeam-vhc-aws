using Xunit;
using VeeamVhcAws.Web.Services;

namespace VeeamVhcAws.Tests.Web;

public class OutputFieldTextTests
{
    [Fact]
    public void Format_JoinsAnAddressList()
    {
        var addresses = new List<object> { "ops@example.com", "oncall@example.com" };
        Assert.Equal("ops@example.com, oncall@example.com", OutputFieldText.Format(addresses));
    }

    [Fact]
    public void Format_KeepsASingleAddressString()
    {
        Assert.Equal("ops@example.com", OutputFieldText.Format("ops@example.com"));
    }
}
