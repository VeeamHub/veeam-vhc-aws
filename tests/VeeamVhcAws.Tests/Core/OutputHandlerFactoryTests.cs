using System.Reflection;
using Xunit;
using VeeamVhcAws.Core.Output;
using VeeamVhcAws.Outputs;

namespace VeeamVhcAws.Tests.Core;

public class OutputHandlerFactoryTests
{
    [Fact]
    public void CreateHandlers_SplitsCommaSeparatedEmailRecipients()
    {
        var handlers = OutputHandlerFactory.CreateHandlers(new Dictionary<string, object>
        {
            ["output"] = new List<object>
            {
                new Dictionary<string, object>
                {
                    ["type"] = "email",
                    ["smtp_host"] = "smtp.example",
                    ["from_addr"] = "vhc@example",
                    ["to_addrs"] = "one@example, two@example",
                }
            }
        });

        var email = Assert.IsType<EmailHandler>(Assert.Single(handlers));
        var recipients = (List<string>)typeof(EmailHandler)
            .GetField("_toAddrs", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(email)!;

        Assert.Equal(new[] { "one@example", "two@example" }, recipients);
    }
}
