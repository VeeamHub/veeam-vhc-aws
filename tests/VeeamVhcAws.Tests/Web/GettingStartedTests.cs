using Xunit;
using VeeamVhcAws.Web.Services;

namespace VeeamVhcAws.Tests.Web;

public class GettingStartedTests
{
    private static readonly DateTime Now = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

    private static ConfigSnapshot Config(IEnumerable<ServerView>? servers = null, IEnumerable<string>? outputs = null) =>
        new("c.yaml", true,
            (servers ?? []).ToList(),
            (outputs ?? []).Select(t => new OutputView(t, new())).ToList(),
            new(), new(), new(), new(), new());

    private static StateSnapshot State(DateTime? lastRun = null, params string[] successKeys) =>
        new(lastRun, [], [], successKeys.ToDictionary(k => k, _ => Now));

    private static ServerView Vbr(string name) => new(name, "vbr", "https://" + name, "u", true, "");
    private static ServerView Vbaws(string name) => new(name, "vbaws", "https://" + name, "u", true, "");

    private static bool[] Done(ConfigSnapshot c, StateSnapshot s) =>
        GettingStarted.Evaluate(c, s).Select(x => x.Done).ToArray();

    [Fact]
    public void EmptyConfig_HasFourOrderedStepsNoneDone()
    {
        var steps = GettingStarted.Evaluate(Config(), State());
        Assert.Equal(
            ["Add a server", "Let it connect", "Send alerts somewhere", "Wait for the first scheduled run"],
            steps.Select(x => x.Title));
        Assert.All(steps, x => Assert.False(x.Done));
        Assert.Equal(["servers", "help#how-do-i-test-a-server", "outputs", "help#set-it-up-from-scratch"],
            steps.Select(x => x.Href));
        Assert.Contains("test-connection -c <yaml>", steps[1].HowTo);
        Assert.Contains("5 minutes", steps[3].HowTo);
    }

    [Fact]
    public void ServersWithoutSuccess_OnlyStepOneDone()
    {
        Assert.Equal([true, false, false, false], Done(Config([Vbr("a")]), State()));
    }

    [Fact]
    public void OneOfTwoServersSucceeded_StepTwoNotDone()
    {
        var c = Config([Vbr("a"), Vbr("b")]);
        Assert.False(Done(c, State(null, "a:repo_health"))[1]);
        Assert.True(Done(c, State(null, "a:repo_health", "b:repo_health"))[1]);
    }

    [Fact]
    public void VbawsServer_IsKeyedByWorkerHealth()
    {
        var c = Config([Vbaws("w")]);
        Assert.False(Done(c, State(null, "w:repo_health"))[1]);
        Assert.True(Done(c, State(null, "w:worker_health"))[1]);
    }

    [Fact]
    public void StdoutOnly_StepThreeNotDone()
    {
        Assert.False(Done(Config(outputs: ["json_stdout"]), State())[2]);
    }

    [Theory]
    [InlineData("json_file")]
    [InlineData("prometheus")]
    [InlineData("email")]
    [InlineData("webhook")]
    public void SingleRealOutput_PassesStepThree(string type)
    {
        Assert.True(Done(Config(outputs: [type]), State())[2]);
    }

    [Fact]
    public void LastRun_DrivesStepFour()
    {
        Assert.False(Done(Config(), State())[3]);
        Assert.True(Done(Config(), State(Now))[3]);
    }

    [Fact]
    public void EverythingDone_AllStepsDone()
    {
        var c = Config([Vbr("a"), Vbaws("w")], ["webhook"]);
        var s = State(Now, "a:repo_health", "w:worker_health");
        Assert.All(GettingStarted.Evaluate(c, s), x => Assert.True(x.Done));
    }
}
