using SerialScout.App.ViewModels;

namespace SerialScout.App.Tests;

/// <summary>
/// Tests for the command doubles: async re-entrancy guard, error reporting, and
/// manual can-exchange notification.
/// </summary>
public sealed class RelayCommandTests
{
    [Fact]
    public void RelayCommandEvaluatesGuardAndRaisesManualRequery()
    {
        var enabled = false;
        var executed = 0;
        var command = new RelayCommand(_ => executed++, _ => enabled);
        var requeryRaised = 0;
        command.CanExecuteChanged += (_, _) => requeryRaised++;

        Assert.False(command.CanExecute(null));
        command.Execute(null);
        Assert.Equal(0, executed);

        enabled = true;
        command.RaiseCanExecuteChanged();
        Assert.True(command.CanExecute(null));
        command.Execute(null);
        Assert.Equal(1, executed);
        Assert.Equal(1, requeryRaised);
    }

    [Fact]
    public async Task AsyncCommandBlocksReEntrancyWhileRunning()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;
        var command = new AsyncRelayCommand(
            async () =>
            {
                runs++;
                await gate.Task;
            },
            _ => { });

        command.Execute(null);
        Assert.Equal(1, runs);
        Assert.False(command.CanExecute(null));

        command.Execute(null); // must be ignored while the first run is in flight
        Assert.Equal(1, runs);

        gate.SetResult();
        await WaitUntilIdleAsync(command);
        Assert.True(command.CanExecute(null));
    }

    [Fact]
    public async Task AsyncCommandReportsFailuresInsteadOfFaulting()
    {
        string? reported = null;
        var command = new AsyncRelayCommand(
            () => throw new InvalidOperationException("boom"),
            message => reported = message);

        command.Execute(null);
        await WaitUntilIdleAsync(command);

        Assert.Equal("boom", reported);
        Assert.True(command.CanExecute(null));
    }

    private static async Task WaitUntilIdleAsync(AsyncRelayCommand command)
    {
        for (var i = 0; i < 100 && !command.CanExecute(null); i++)
        {
            await Task.Delay(5);
        }
    }
}
