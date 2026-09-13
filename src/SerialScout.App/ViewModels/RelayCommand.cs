using System.Windows.Input;

namespace SerialScout.App.ViewModels;

/// <summary>
/// Synchronous <see cref="ICommand"/> double used by the view models. Can-execute state is
/// re-raised manually by the owning view model (<see cref="RaiseCanExecuteChanged"/>) so
/// the command never depends on framework-wide requery hooks; XAML additionally binds
/// <c>IsEnabled</c> to view-model state properties for the same reason.
/// </summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    /// <summary>Creates a command.</summary>
    /// <param name="execute">Action invoked on execute.</param>
    /// <param name="canExecute">Optional guard evaluated by <see cref="CanExecute(object?)"/>.</param>
    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        ArgumentNullException.ThrowIfNull(execute);
        _execute = execute;
        _canExecute = canExecute;
    }

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged;

    /// <inheritdoc />
    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    /// <inheritdoc />
    public void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _execute(parameter);
    }

    /// <summary>Notifies bound controls that <see cref="CanExecute(object?)"/> may have changed.</summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// Asynchronous <see cref="ICommand"/> double: re-entrancy is blocked while a run is in
/// flight, and unhandled exceptions are surfaced through an injected report callback
/// instead of crashing the UI thread or faulting silently.
/// </summary>
public sealed class AsyncRelayCommand : ICommand
{
    private const int Idle = 0;
    private const int Running = 1;

    private readonly Func<Task> _execute;
    private readonly Action<string> _reportError;
    private readonly Func<bool>? _canExecute;
    private int _status;
    private Task? _running;

    /// <summary>Creates an async command.</summary>
    /// <param name="execute">Async action invoked on execute.</param>
    /// <param name="reportError">Receives the message of any unhandled exception.</param>
    /// <param name="canExecute">Optional guard evaluated by <see cref="CanExecute(object?)"/>.</param>
    public AsyncRelayCommand(Func<Task> execute, Action<string> reportError, Func<bool>? canExecute = null)
    {
        ArgumentNullException.ThrowIfNull(execute);
        ArgumentNullException.ThrowIfNull(reportError);
        _execute = execute;
        _reportError = reportError;
        _canExecute = canExecute;
    }

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged;

    /// <inheritdoc />
    public bool CanExecute(object? parameter)
        => Volatile.Read(ref _status) == Idle && (_canExecute?.Invoke() ?? true);

    /// <inheritdoc />
    public void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _ = ExecuteAsync();
    }

    /// <summary>
    /// Starts the run (if idle) and returns its task so tests can await completion;
    /// re-entrant calls return the in-flight task without starting a second run.
    /// </summary>
    public Task ExecuteAsync()
    {
        if (Interlocked.CompareExchange(ref _status, Running, Idle) != Idle)
        {
            return Volatile.Read(ref _running) ?? Task.CompletedTask;
        }

        RaiseCanExecuteChanged();
        var task = RunAsync();
        Volatile.Write(ref _running, task);
        return task;
    }

    /// <summary>Notifies bound controls that <see cref="CanExecute(object?)"/> may have changed.</summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    private async Task RunAsync()
    {
        try
        {
            await _execute().ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Top of the command stack: anything escaping here would
        // fault the UI thread, so it is funneled to the error-report callback instead.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _reportError(ex.Message);
        }
        finally
        {
            Volatile.Write(ref _status, Idle);
            RaiseCanExecuteChanged();
        }
    }
}
