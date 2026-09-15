using SerialScout.App;

namespace SerialScout.App.Tests;

public sealed class PackagedSmokeTests
{
    [Fact]
    public void RunPersistsThroughReopenWithoutLeavingTemporaryStorage()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = PackagedSmoke.Run(output, error);

        Assert.Equal(0, exitCode);
        Assert.Contains("SQLite create/write/close/reopen/read succeeded", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("temporary storage removed", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());
    }
}
