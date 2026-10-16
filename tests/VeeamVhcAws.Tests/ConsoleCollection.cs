using Xunit;

namespace VeeamVhcAws.Tests;

// Tests here swap process-wide state (Console, Serilog Log.Logger), so they
// must not run alongside any other test class.
[CollectionDefinition("console", DisableParallelization = true)]
public class ConsoleCollection
{
}
