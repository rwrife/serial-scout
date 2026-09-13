using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SerialScout.App.ViewModels;

namespace SerialScout.App;

/// <summary>
/// Application shell window. The DataContext is the <see cref="MainViewModel"/>; the
/// code-behind only hosts click handlers that need dialog services (save-log,
/// draft-from-selection) and the error-banner dismissal.
/// </summary>
public partial class MainWindow : Window
{
    private static readonly string[] TextFilePatterns = ["*.txt"];

    private MainViewModel ViewModel => (MainViewModel)DataContext!;

    /// <summary>Creates the shell window for the given view model.</summary>
    public MainWindow(MainViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        InitializeComponent();
        Opened += (_, _) => _ = ViewModel.StartAsync();
    }

    private void OnDismissError(object? sender, RoutedEventArgs e) => ViewModel.ClearError();

    private void OnDraftFromSelection(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.Devices.SelectedDevice is null)
        {
            ViewModel.LastError = "Select a device first.";
            return;
        }

        if (ViewModel.Devices.TryCreateDraftFromSelection(ViewModel.Editor))
        {
            ViewModel.SelectedTabIndex = 1;
        }
    }

    private async void OnSaveLog(object? sender, RoutedEventArgs e)
    {
        var options = new FilePickerSaveOptions
        {
            Title = "Save session log",
            SuggestedFileName = "serial-scout-log.txt",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("Text file") { Patterns = TextFilePatterns },
            },
        };

        var target = await StorageProvider.SaveFilePickerAsync(options);
        if (target is not null)
        {
            await ViewModel.Terminal.SaveLogToFileAsync(
                target.Path.LocalPath,
                message => ViewModel.LastError = message);
        }
    }
}
