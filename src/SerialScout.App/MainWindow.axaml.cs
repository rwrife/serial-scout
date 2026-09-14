using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Microsoft.Data.Sqlite;
using SerialScout.App.ViewModels;
using SerialScout.Core.Privacy;

namespace SerialScout.App;

/// <summary>
/// Application shell window. The DataContext is the <see cref="MainViewModel"/>; the
/// code-behind only hosts click handlers that need dialog services (save-log,
/// draft-from-selection) and the error-banner dismissal.
/// </summary>
public partial class MainWindow : Window
{
    private static readonly string[] TextFilePatterns = ["*.txt"];
    private static readonly string[] JsonFilePatterns = ["*.json"];

    private MainViewModel ViewModel => (MainViewModel)DataContext!;

    /// <summary>Creates the XAML-loadable shell; production assigns a view model through the other constructor.</summary>
    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>Creates the shell window for the given view model.</summary>
    public MainWindow(MainViewModel viewModel)
        : this()
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
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

    private void OnOpenPrivacy(object? sender, RoutedEventArgs e)
    {
        RunUiOperation(() =>
        {
            ViewModel.Privacy.RefreshSessions();
            ViewModel.SelectedTabIndex = 3;
        });
    }

    private void OnRefreshPrivacySessions(object? sender, RoutedEventArgs e)
        => RunUiOperation(ViewModel.Privacy.RefreshSessions);

    private void OnCreateExportPreview(object? sender, RoutedEventArgs e)
        => RunUiOperation(ViewModel.Privacy.CreateExportPreview);

    private async void OnWriteExport(object? sender, RoutedEventArgs e)
    {
        await RunUiOperationAsync(async () =>
        {
            var extension = ViewModel.Privacy.ExportFileExtension;
            var options = new FilePickerSaveOptions
            {
                Title = "Export reviewed session copy",
                SuggestedFileName = "serial-scout-session" + extension,
                FileTypeChoices = new[]
                {
                    extension == ".json"
                        ? new FilePickerFileType("JSON file") { Patterns = JsonFilePatterns }
                        : new FilePickerFileType("Text file") { Patterns = TextFilePatterns },
                },
            };

            var target = await StorageProvider.SaveFilePickerAsync(options);
            if (target is not null)
            {
                await ViewModel.Privacy.WriteExportAsync(target.Path.LocalPath);
            }
        });
    }

    private void OnCreateBackupPreview(object? sender, RoutedEventArgs e)
        => RunUiOperation(ViewModel.Privacy.CreateBackupPreview);

    private async void OnWriteBackup(object? sender, RoutedEventArgs e)
    {
        await RunUiOperationAsync(async () =>
        {
            var target = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save sensitive Serial Scout backup",
                SuggestedFileName = "serial-scout-backup.json",
                FileTypeChoices = new[] { new FilePickerFileType("JSON backup") { Patterns = JsonFilePatterns } },
            });
            if (target is not null)
            {
                await ViewModel.Privacy.WriteBackupAsync(target.Path.LocalPath);
            }
        });
    }

    private async void OnRestoreBackup(object? sender, RoutedEventArgs e)
    {
        if (!ViewModel.Privacy.IsSensitiveBackupConfirmed)
        {
            ViewModel.LastError = "Acknowledge the sensitive backup warning before restore.";
            return;
        }

        await RunUiOperationAsync(async () =>
        {
            var selected = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Restore untrusted backup as new local records",
                AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType("JSON backup") { Patterns = JsonFilePatterns } },
            });
            var source = selected.Count > 0 ? selected[0] : null;
            if (source is not null)
            {
                var content = await BackupService.ReadBoundedAsync(source.Path.LocalPath);
                await ViewModel.Privacy.RestoreAsync(content);
            }
        });
    }

    private void OnApplyRetention(object? sender, RoutedEventArgs e)
    {
        RunUiOperation(ViewModel.Privacy.ApplyRetention);
    }

    private async Task RunUiOperationAsync(Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (Exception ex) when (IsExpectedUiException(ex))
        {
            ViewModel.LastError = ex.Message;
        }
    }

    private void RunUiOperation(Action operation)
    {
        try
        {
            operation();
        }
        catch (Exception ex) when (IsExpectedUiException(ex))
        {
            ViewModel.LastError = ex.Message;
        }
    }

    private static bool IsExpectedUiException(Exception ex)
        => ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException or SqliteException;
}
