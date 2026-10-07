using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace PGAssetTool.Gui.Views;

public partial class MapsWindow : Window
{
    public MapsWindow()
    {
        AvaloniaXamlLoader.Load(this);
        // Straight to typing a name: two hundred maps are found by name, not by scrolling.
        Opened += (_, _) => this.FindControl<TextBox>("Filter")?.Focus();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
