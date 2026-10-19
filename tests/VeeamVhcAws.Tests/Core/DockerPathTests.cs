using Xunit;

namespace VeeamVhcAws.Tests.Core;

public class DockerPathTests
{
    [Fact]
    public void ImageCommand_UsesTheReadmeMountPath()
    {
        var root = RepoRoot();
        var docker = File.ReadAllText(Path.Combine(root, "Dockerfile"));
        var readme = File.ReadAllText(Path.Combine(root, "README.md"));

        Assert.Contains("/config/veeam-vhc-aws.yaml", docker);
        Assert.DoesNotContain("/config/config.yaml", docker);
        Assert.Contains("/config/veeam-vhc-aws.yaml", readme);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Dockerfile")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
