using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using MySqlPunk.Core.Services;

namespace MySqlPunk.Desktop;

/// <summary>
/// Read-only preview of a schema synchronization script. It deliberately offers only copy and save: the main
/// editor is connected to the comparison source, so sending the script there would run it on the wrong side.
/// </summary>
internal sealed class SyncScriptWindow : Window
{
    private readonly SchemaSyncScript _script;
    private readonly TextBlock _status;

    public SyncScriptWindow(SchemaSyncScript script, string targetDescription)
    {
        _script = script;
        Title = "同步 SQL 預覽";
        Width = 960;
        Height = 700;
        MinWidth = 640;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var header = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#1B2B4B")),
            Padding = new Thickness(18, 12),
            Child = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock { Text = "同步 SQL 預覽（不會自動執行）", Foreground = Brushes.White, FontSize = 17, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = $"請在目標執行：{targetDescription}", Foreground = new SolidColorBrush(Color.Parse("#FEC84B")), FontSize = 12 },
                    new TextBlock { Text = script.Summary, Foreground = new SolidColorBrush(Color.Parse("#B9C7E8")), FontSize = 12 }
                }
            }
        };

        var editor = new TextBox
        {
            Text = script.Text,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Cascadia Mono, JetBrains Mono, Menlo, monospace"),
            FontSize = 13,
            Margin = new Thickness(12, 10, 12, 0)
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(editor, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(editor, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);

        _status = new TextBlock
        {
            Text = "破壞性變更已以註解呈現；執行前請先備份目標並逐項確認。",
            Foreground = new SolidColorBrush(Color.Parse("#667085")),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var copy = new Button { Content = "複製", Margin = new Thickness(0, 0, 8, 0) };
        copy.Click += async (_, _) => await CopyAsync();
        var save = new Button { Content = "另存 .sql…", Margin = new Thickness(0, 0, 8, 0) };
        save.Click += async (_, _) => await SaveAsync();
        var close = new Button { Content = "關閉", Padding = new Thickness(14, 6) };
        close.Click += (_, _) => Close();

        var footer = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"),
            Margin = new Thickness(12, 10, 12, 12)
        };
        footer.Children.Add(_status);
        Grid.SetColumn(copy, 1);
        Grid.SetColumn(save, 2);
        Grid.SetColumn(close, 3);
        footer.Children.Add(copy);
        footer.Children.Add(save);
        footer.Children.Add(close);

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        root.Children.Add(header);
        Grid.SetRow(editor, 1);
        Grid.SetRow(footer, 2);
        root.Children.Add(editor);
        root.Children.Add(footer);
        Content = root;
    }

    private async Task CopyAsync()
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            _status.Text = "目前桌面環境未提供系統剪貼簿。";
            return;
        }

        await clipboard.SetTextAsync(_script.Text);
        await clipboard.FlushAsync();
        _status.Text = "已複製到剪貼簿。";
    }

    private async Task SaveAsync()
    {
        if (!StorageProvider.CanSave)
        {
            _status.Text = "目前桌面環境未提供儲存檔案對話框。";
            return;
        }

        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "另存同步 SQL",
                SuggestedFileName = $"mysqlpunk-schema-sync-{DateTime.Now:yyyyMMdd-HHmmss}.sql",
                DefaultExtension = "sql",
                FileTypeChoices = new[] { new FilePickerFileType("SQL 指令碼") { Patterns = new[] { "*.sql" }, MimeTypes = new[] { "application/sql" } } }
            });
            if (file?.TryGetLocalPath() is not { } path)
            {
                return;
            }

            var (bytes, written) = await HtmlReportFile.WriteAsync(_script.Text, path);
            _status.Text = $"已儲存（{bytes / 1024d:N1} KB）：{written}";
        }
        catch (Exception exception)
        {
            _status.Text = $"無法儲存：{exception.Message}";
        }
    }
}
