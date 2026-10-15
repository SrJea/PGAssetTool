using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using PGAssetTool.Core.Settings;
using PGAssetTool.Gui.ViewModels;

namespace PGAssetTool.Gui.Views;

/// Preferences, applied as they are changed rather than on an OK button. There is nothing here
/// worth a cancel: every setting takes effect immediately and can be set back the same way.
public partial class OptionsWindow : Window
{
    public OptionsWindow()
    {
        AvaloniaXamlLoader.Load(this);

        // No taller than most of the screen it opens on. The window sizes itself to its content and
        // cannot be resized, so with every explanation open it is taller than a 1080p screen and the
        // Close button hangs off the bottom. Capped here, the list scrolls and the button stays.
        Opened += (_, _) =>
        {
            if (Screens.ScreenFromWindow(this) is { } screen)
                MaxHeight = screen.WorkingArea.Height / screen.Scaling * 0.85;
        };
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    /// Picks the game's folder. Typing the path works just as well; this is for the far more
    /// common case of knowing where it is without knowing how to spell it.
    private async void OnChooseGame(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel model) return;

        try
        {
            var chosen = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Where the game is installed",
                AllowMultiple = false,
            });

            if (chosen.FirstOrDefault()?.TryGetLocalPath() is { Length: > 0 } path)
                model.GameDirectory = path;
        }
        catch (Exception ex)
        {
            model.Status = ErrorLog.Said(ex, "choosing the game's folder");
        }
    }
}
