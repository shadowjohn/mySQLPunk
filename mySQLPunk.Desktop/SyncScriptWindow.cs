using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using MySqlPunk.Core.Models;
using MySqlPunk.Core.Services;

namespace MySqlPunk.Desktop;

/// <summary>
/// Schema synchronization script preview. The script tab is read-only (copy／save). When the owner supplies a
/// target executor, a second tab lets the user tick statements and run them on the TARGET only — never through
/// the main editor, which is connected to the comparison source. Destructive statements start unticked and
/// require typing the target database name.
/// </summary>
internal sealed class SyncScriptWindow : Window
{
    private readonly SchemaSyncScript _script;
    private readonly TextBlock _status;
    private readonly string _targetDatabase;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<StatementBatchResult>>? _execute;
    private readonly List<ExecutionRow> _rows = new();
    private readonly StackPanel _rowsPanel = new() { Spacing = 4 };
    private readonly TextBlock _executionStatus = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly Button _executeButton = new() { Content = "在目標執行勾選的語句…", Padding = new Thickness(14, 6) };
    private readonly CancellationTokenSource _closing = new();
    private bool _running;

    public SyncScriptWindow(
        SchemaSyncScript script,
        string targetDescription,
        string targetDatabase = "",
        Func<IReadOnlyList<string>, CancellationToken, Task<StatementBatchResult>>? execute = null)
    {
        _script = script;
        _targetDatabase = targetDatabase;
        _execute = execute;
        Title = "同步 SQL 預覽";
        Width = 980;
        Height = 720;
        MinWidth = 640;
        MinHeight = 440;
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
                    new TextBlock { Text = "同步 SQL 預覽", Foreground = Brushes.White, FontSize = 17, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = $"目標：{targetDescription}", Foreground = new SolidColorBrush(Color.Parse("#FEC84B")), FontSize = 12 },
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
            Margin = new Thickness(0, 6, 0, 0)
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(editor, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(editor, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);

        var tabs = new TabControl { Margin = new Thickness(12, 8, 12, 0) };
        tabs.Items.Add(new TabItem { Header = "腳本", Content = editor, FontSize = 14 });
        if (execute is not null && script.Statements.Count + script.DestructiveStatements.Count > 0)
        {
            tabs.Items.Add(new TabItem { Header = "在目標執行", Content = BuildExecutionPanel(), FontSize = 14 });
        }

        _status = new TextBlock
        {
            Text = "腳本不會自動執行；刪除類變更預設不勾選。",
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
        Grid.SetRow(tabs, 1);
        Grid.SetRow(footer, 2);
        root.Children.Add(tabs);
        root.Children.Add(footer);
        Content = root;

        Closing += (_, args) =>
        {
            if (_running)
            {
                // Closing mid-batch would abandon a transaction half way; wait for the batch to finish.
                args.Cancel = true;
                _executionStatus.Text = "正在執行，請等待完成後再關閉。";
                return;
            }

            _closing.Cancel();
        };
    }

    /// <summary>True once any statement has run on the target, so the caller can refresh the comparison.</summary>
    public bool ExecutedOnTarget { get; private set; }

    private Control BuildExecutionPanel()
    {
        var index = 0;
        foreach (var sql in _script.Statements)
        {
            _rows.Add(new ExecutionRow(index++, sql, destructive: false, description: string.Empty));
        }

        for (var destructiveIndex = 0; destructiveIndex < _script.DestructiveStatements.Count; destructiveIndex++)
        {
            _rows.Add(new ExecutionRow(
                index++,
                _script.DestructiveStatements[destructiveIndex],
                destructive: true,
                description: _script.DestructiveItems.ElementAtOrDefault(destructiveIndex) ?? string.Empty));
        }

        foreach (var row in _rows)
        {
            _rowsPanel.Children.Add(row.View);
        }

        var selectAll = new Button { Content = "全選非破壞性", Margin = new Thickness(0, 0, 8, 0) };
        selectAll.Click += (_, _) => _rows.ForEach(row => row.Check.IsChecked = !row.Destructive);
        var selectNone = new Button { Content = "全不選" };
        selectNone.Click += (_, _) => _rows.ForEach(row => row.Check.IsChecked = false);
        _executeButton.Click += async (_, _) => await ExecuteAsync();

        var notice = new TextBlock
        {
            Text = "只會在上方標示的「目標」連線執行，不會動到來源。PostgreSQL／SQL Server／SQLite 以單一交易執行、失敗全部回滾；" +
                   "MySQL／MariaDB 的 DDL 會自動提交，遇到錯誤會停止並列出已生效的語句。勾選紅色的破壞性語句時需輸入目標資料庫名稱確認。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#B54708"))
        };

        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0, Margin = new Thickness(0, 8, 0, 8) };
        toolbar.Children.Add(selectAll);
        toolbar.Children.Add(selectNone);

        var bottom = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 8, 0, 0) };
        bottom.Children.Add(_executionStatus);
        Grid.SetColumn(_executeButton, 1);
        bottom.Children.Add(_executeButton);

        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), Margin = new Thickness(0, 6, 0, 0) };
        layout.Children.Add(notice);
        Grid.SetRow(toolbar, 1);
        layout.Children.Add(toolbar);
        var scroller = new ScrollViewer { Content = _rowsPanel };
        Grid.SetRow(scroller, 2);
        layout.Children.Add(scroller);
        Grid.SetRow(bottom, 3);
        layout.Children.Add(bottom);
        return layout;
    }

    private async Task ExecuteAsync()
    {
        if (_execute is null || _running)
        {
            return;
        }

        var selected = _rows.Where(row => row.Check.IsChecked == true).ToList();
        if (selected.Count == 0)
        {
            _executionStatus.Text = "請先勾選要執行的語句。";
            return;
        }

        var destructive = selected.Count(row => row.Destructive);
        var confirmed = destructive > 0
            ? await TypedConfirmation.ShowAsync(
                this,
                $"即將在目標資料庫「{_targetDatabase}」執行 {selected.Count} 個語句，其中 {destructive} 個會刪除資料表、欄位或約束，資料無法復原。\n請先確認已備份，並輸入目標資料庫名稱以繼續：",
                _targetDatabase)
            : await MessageDialog.ShowAsync(
                this,
                "在目標執行",
                $"即將在目標資料庫「{_targetDatabase}」執行 {selected.Count} 個語句。確定繼續？",
                showCancel: true,
                confirmText: "執行");
        if (!confirmed)
        {
            return;
        }

        _running = true;
        _executeButton.IsEnabled = false;
        _rows.ForEach(row => row.Check.IsEnabled = false);
        _executionStatus.Text = "正在目標執行…";
        foreach (var row in _rows)
        {
            row.SetResult(null);
        }

        try
        {
            // Additive statements first (script order), destructive ones after them.
            var ordered = selected.OrderBy(row => row.Destructive).ThenBy(row => row.Index).ToList();
            var result = await _execute(ordered.Select(row => row.Sql).ToList(), _closing.Token);
            ExecutedOnTarget = ExecutedOnTarget || result.Statements.Any(item => item.Outcome == StatementOutcome.Succeeded);
            for (var position = 0; position < ordered.Count; position++)
            {
                ordered[position].SetResult(result.Statements[position]);
            }

            _executionStatus.Text = result.Summary;
            _executionStatus.Foreground = new SolidColorBrush(Color.Parse(result.Succeeded ? "#067647" : "#B42318"));
        }
        catch (Exception exception)
        {
            _executionStatus.Text = $"執行失敗：{exception.Message}";
            _executionStatus.Foreground = new SolidColorBrush(Color.Parse("#B42318"));
        }
        finally
        {
            _running = false;
            _executeButton.IsEnabled = true;
            _rows.ForEach(row => row.Check.IsEnabled = true);
        }
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

    private sealed class ExecutionRow
    {
        private readonly TextBlock _result = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };

        public ExecutionRow(int index, string sql, bool destructive, string description)
        {
            Index = index;
            Sql = sql;
            Destructive = destructive;
            Check = new CheckBox { IsChecked = !destructive, VerticalAlignment = VerticalAlignment.Top };
            var text = new StackPanel { Spacing = 2 };
            if (destructive)
            {
                text.Children.Add(new TextBlock
                {
                    Text = "破壞性：" + description,
                    Foreground = new SolidColorBrush(Color.Parse("#B42318")),
                    FontWeight = FontWeight.SemiBold,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap
                });
            }

            text.Children.Add(new TextBlock
            {
                Text = sql,
                FontFamily = new FontFamily("Cascadia Mono, JetBrains Mono, Menlo, monospace"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = destructive ? new SolidColorBrush(Color.Parse("#B42318")) : Brushes.Black
            });
            text.Children.Add(_result);
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            grid.Children.Add(Check);
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
            View = new Border
            {
                BorderBrush = new SolidColorBrush(Color.Parse("#E4E7EC")),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(4, 6),
                Child = grid
            };
        }

        public int Index { get; }

        public string Sql { get; }

        public bool Destructive { get; }

        public CheckBox Check { get; }

        public Control View { get; }

        public void SetResult(StatementExecution? execution)
        {
            if (execution is null)
            {
                _result.Text = string.Empty;
                return;
            }

            (_result.Text, var color) = execution.Outcome switch
            {
                StatementOutcome.Succeeded => ($"✓ 成功（{execution.Elapsed.TotalMilliseconds:N0} ms）", "#067647"),
                StatementOutcome.Failed => ($"✗ 失敗：{execution.Message}", "#B42318"),
                StatementOutcome.RolledBack => ("↺ 已執行但隨交易回滾", "#B54708"),
                _ => ("— 未執行", "#667085")
            };
            _result.Foreground = new SolidColorBrush(Color.Parse(color));
        }
    }
}

/// <summary>Confirmation that only enables its button when the exact expected text is typed.</summary>
internal sealed class TypedConfirmation : Window
{
    private TypedConfirmation(string message, string expected)
    {
        Title = "確認破壞性變更";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var input = new TextBox { PlaceholderText = expected };
        var confirm = new Button { Content = "我了解，執行", IsEnabled = false, Padding = new Thickness(14, 7) };
        var cancel = new Button { Content = "取消", Padding = new Thickness(14, 7) };
        input.TextChanged += (_, _) => confirm.IsEnabled = string.Equals(input.Text, expected, StringComparison.Ordinal);
        confirm.Click += (_, _) => Close(true);
        cancel.Click += (_, _) => Close(false);
        Content = new Border
        {
            Padding = new Thickness(22),
            Child = new StackPanel
            {
                Spacing = 14,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    input,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { cancel, confirm }
                    }
                }
            }
        };
    }

    public static async Task<bool> ShowAsync(Window owner, string message, string expected)
    {
        if (string.IsNullOrEmpty(expected))
        {
            return false;
        }

        return await new TypedConfirmation(message, expected).ShowDialog<bool>(owner);
    }
}
