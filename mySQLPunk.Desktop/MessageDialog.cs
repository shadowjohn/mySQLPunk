using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace MySqlPunk.Desktop;

internal sealed class MessageDialog : Window
{
    private MessageDialog(string title, string message, bool showCancel)
    {
        Title = title;
        Width = 470;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var confirm = new Button
        {
            Content = showCancel ? "確定" : "關閉",
            Padding = new Thickness(14, 7),
            MinWidth = 80,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        confirm.Click += (_, _) => Close(true);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };
        if (showCancel)
        {
            var cancel = new Button
            {
                Content = "取消",
                Padding = new Thickness(14, 7),
                MinWidth = 80,
                HorizontalContentAlignment = HorizontalAlignment.Center
            };
            cancel.Click += (_, _) => Close(false);
            buttons.Children.Add(cancel);
        }

        buttons.Children.Add(confirm);
        Content = new Border
        {
            Padding = new Thickness(22),
            Child = new StackPanel
            {
                Spacing = 20,
                Children =
                {
                    new TextBlock
                    {
                        Text = message,
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap
                    },
                    buttons
                }
            }
        };
    }

    public static Task<bool> ShowAsync(Window owner, string title, string message, bool showCancel)
    {
        return new MessageDialog(title, message, showCancel).ShowDialog<bool>(owner);
    }
}
