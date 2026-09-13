namespace SerialScout.App;

/// <summary>
/// Indirection over the UI-thread scheduler so view models never reference
/// <c>Avalonia.Threading</c> directly and stay unit-testable headless. The application
/// installs the real Avalonia dispatcher implementations during framework startup;
/// tests keep the defaults (run inline / never repeat).
/// </summary>
public static class Ui
{
    /// <summary>Default: execute the action inline (no UI thread exists in tests).</summary>
    public static Action<Action> DefaultPost { get; } = static action => action();

    /// <summary>Default: no repeating callbacks; returns a no-op stop handle.</summary>
    public static Func<Action, TimeSpan, Action> DefaultRepeating { get; } =
        static (_, _) => static () => { };

    /// <summary>Posts an action for execution on the UI thread.</summary>
    public static Action<Action> Post { get; set; } = DefaultPost;

    /// <summary>
    /// Starts invoking <paramref name="action"/> every <paramref name="interval"/> on the
    /// UI thread and returns a handle that stops it.
    /// </summary>
    public static Func<Action, TimeSpan, Action> Repeating { get; set; } = DefaultRepeating;
}
