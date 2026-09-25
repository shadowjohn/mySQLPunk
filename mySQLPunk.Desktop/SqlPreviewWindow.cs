using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace MySqlPunk.Desktop;

/// <summary>Read-only monospace preview of SQL text with a close button; Escape closes it.</summary>
internal static class SqlPreviewWindow
{
    public static Task ShowAsync(Window owner, string title, string text)
    {
        var box = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Cascadia Mono, JetBrains Mono, Menlo, monospace"),
            FontSize = 12,
            Margin = new Thickness(12)
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(box, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        var preview = new Window
        {
            Title = title,
            Width = 960,
            Height = 640,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var closeButton = new Button { Content = "關閉", Padding = new Thickness(14, 6), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12, 0, 12, 12) };
        closeButton.Click += (_, _) => preview.Close();
        // Tunnel so the focused read-only TextBox cannot swallow Escape before the window sees it.
        preview.AddHandler(
            Avalonia.Input.InputElement.KeyDownEvent,
            (_, args) =>
            {
                if (args.Key == Avalonia.Input.Key.Escape)
                {
                    args.Handled = true;
                    preview.Close();
                }
            },
            Avalonia.Interactivity.RoutingStrategies.Tunnel);
        var layout = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        layout.Children.Add(box);
        Grid.SetRow(closeButton, 1);
        layout.Children.Add(closeButton);
        preview.Content = layout;
        return preview.ShowDialog(owner);
    }
}
