using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MySqlPunk.Core.Models;
using MySqlPunk.Core.Providers;
using MySqlPunk.Core.Services;

namespace MySqlPunk.Desktop;

/// <summary>
/// Test-data generator for the tables of the current database. Rules are edited per column (Auto infers from type,
/// name, constraints and foreign keys); previewing only generates in memory, and writing asks for confirmation and
/// runs every table in one transaction.
/// </summary>
internal sealed class DataGeneratorWindow : Window
{
    private static readonly (DataGeneratorRuleKind Kind, string Label, string Hint)[] RuleChoices =
    {
        (DataGeneratorRuleKind.Auto, "自動", string.Empty),
        (DataGeneratorRuleKind.DatabaseDefault, "資料庫預設", string.Empty),
        (DataGeneratorRuleKind.Null, "NULL", string.Empty),
        (DataGeneratorRuleKind.Fixed, "固定值", "要寫入的值"),
        (DataGeneratorRuleKind.Sequence, "序列", "起始值[,間隔]，例：1000,10 或 2024-01-01,1"),
        (DataGeneratorRuleKind.Range, "範圍", "最小..最大，例：1..100 或 2024-01-01..2024-12-31"),
        (DataGeneratorRuleKind.List, "清單", "以 | 分隔，例：new|paid|shipped"),
        (DataGeneratorRuleKind.Pattern, "樣式", "例：SKU-{n}-{digits:4}；可用 {n} {int:1-9} {letters:3} {uuid}"),
        (DataGeneratorRuleKind.Dictionary, "字典", "字典名稱，例：zh-TW 姓氏；按下方「字典…」管理")
    };

    private readonly IDatabaseSession _session;
    private readonly string _database;
    private readonly StackPanel _tablePanel = new() { Spacing = 2 };
    private readonly StackPanel _columnPanel = new() { Spacing = 4 };
    private readonly TextBlock _columnHeader = new() { FontWeight = FontWeight.SemiBold, FontSize = 14, Text = "選擇左側資料表以設定欄位規則" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly TextBox _seed = new() { Width = 120, PlaceholderText = "隨機" };
    private readonly Button _previewButton = new() { Content = "產生並預覽 SQL…" };
    private readonly Button _writeButton = new() { Content = "產生並寫入…", Padding = new Thickness(14, 6) };
    private readonly List<TableRow> _rows = new();
    private readonly Dictionary<DatabaseObjectInfo, DataGeneratorTableInfo> _described = new();
    private readonly Dictionary<DatabaseObjectInfo, Dictionary<string, DataGeneratorRule>> _rules = new();
    private readonly CancellationTokenSource _closing = new();
    private bool _busy;

    public DataGeneratorWindow(IDatabaseSession session, string database)
    {
        _session = session;
        _database = database;
        Title = "資料產生器";
        Width = 1180;
        Height = 760;
        MinWidth = 820;
        MinHeight = 500;
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
                    new TextBlock { Text = "資料產生器", Foreground = Brushes.White, FontSize = 17, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = $"寫入目標：{session.Profile.Name}／{database}", Foreground = new SolidColorBrush(Color.Parse("#FEC84B")), FontSize = 12 },
                    new TextBlock
                    {
                        Text = "外鍵欄位會挑選現有或本次產生的資料列；主鍵與唯一欄位不會重複；所有資料表在單一交易中寫入，任何一列失敗就整批回滾。",
                        Foreground = new SolidColorBrush(Color.Parse("#B9C7E8")),
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap
                    }
                }
            }
        };

        var selectAll = new Button { Content = "全選", Margin = new Thickness(0, 0, 6, 0) };
        selectAll.Click += (_, _) => _rows.ForEach(row => row.Check.IsChecked = true);
        var selectNone = new Button { Content = "全不選" };
        selectNone.Click += (_, _) => _rows.ForEach(row => row.Check.IsChecked = false);
        var tableToolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        tableToolbar.Children.Add(selectAll);
        tableToolbar.Children.Add(selectNone);
        var tablesPane = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*") };
        tablesPane.Children.Add(new TextBlock { Text = "資料表與筆數", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        Grid.SetRow(tableToolbar, 1);
        tablesPane.Children.Add(tableToolbar);
        var tableScroller = new ScrollViewer { Content = _tablePanel };
        Grid.SetRow(tableScroller, 2);
        tablesPane.Children.Add(tableScroller);

        var columnsPane = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*"), Margin = new Thickness(14, 0, 0, 0) };
        columnsPane.Children.Add(_columnHeader);
        var legend = new TextBlock
        {
            Text = "「自動」依型別、欄位名稱、唯一性與外鍵決定內容（說明列在各欄位下方）；「資料庫預設」會讓欄位不出現在 INSERT。NULL 比例只套用在可為 NULL 且非唯一的欄位。",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.Parse("#475467")),
            Margin = new Thickness(0, 4, 0, 8)
        };
        Grid.SetRow(legend, 1);
        columnsPane.Children.Add(legend);
        var columnScroller = new ScrollViewer { Content = _columnPanel };
        Grid.SetRow(columnScroller, 2);
        columnsPane.Children.Add(columnScroller);

        var panes = new Grid { ColumnDefinitions = new ColumnDefinitions("320,*") };
        panes.Children.Add(tablesPane);
        Grid.SetColumn(columnsPane, 1);
        panes.Children.Add(columnsPane);

        _previewButton.Click += async (_, _) => await PreviewAsync();
        _writeButton.Click += async (_, _) => await WriteAsync();
        var close = new Button { Content = "關閉", Padding = new Thickness(14, 6), Margin = new Thickness(8, 0, 0, 0) };
        close.Click += (_, _) => Close();
        var dictionaries = new Button { Content = "字典…", Margin = new Thickness(0, 0, 12, 0) };
        dictionaries.Click += async (_, _) => await new DataGeneratorDictionaryWindow(DictionaryDirectory).ShowDialog(this);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
        actions.Children.Add(dictionaries);
        actions.Children.Add(new TextBlock { Text = "Seed", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        actions.Children.Add(_seed);
        actions.Children.Add(new Border { Width = 12 });
        actions.Children.Add(_previewButton);
        actions.Children.Add(new Border { Width = 8 });
        actions.Children.Add(_writeButton);
        actions.Children.Add(close);
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 10, 0, 0) };
        footer.Children.Add(_status);
        Grid.SetColumn(actions, 1);
        footer.Children.Add(actions);

        var body = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), Margin = new Thickness(14, 10, 14, 12) };
        body.Children.Add(panes);
        Grid.SetRow(footer, 1);
        body.Children.Add(footer);
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        root.Children.Add(header);
        Grid.SetRow(body, 1);
        root.Children.Add(body);
        Content = root;

        Opened += async (_, _) => await LoadTablesAsync();
        Closing += (_, args) =>
        {
            if (_busy)
            {
                args.Cancel = true;
                _status.Text = "正在處理，請等待完成後再關閉。";
                return;
            }

            _closing.Cancel();
        };
    }

    private async Task LoadTablesAsync()
    {
        await RunAsync("正在讀取資料表…", async cancellationToken =>
        {
            var tables = (await _session.GetObjectsAsync(_database, cancellationToken))
                .Where(item => item.Kind == DatabaseObjectKind.Table)
                .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var table in tables)
            {
                var row = new TableRow(table);
                row.Selected += async () => await ShowColumnsAsync(row);
                _rows.Add(row);
                _tablePanel.Children.Add(row.View);
            }

            _status.Text = tables.Count == 0
                ? "這個資料庫沒有資料表。"
                : $"共 {tables.Count} 張資料表；勾選要產生的資料表並設定筆數，按資料表名稱可調整欄位規則。";
        });
    }

    private async Task ShowColumnsAsync(TableRow row)
    {
        foreach (var item in _rows)
        {
            item.SetFocused(item == row);
        }

        await RunAsync($"正在讀取 {row.Table.DisplayName} 的欄位…", async cancellationToken =>
        {
            if (!_described.TryGetValue(row.Table, out var info))
            {
                info = await DataGeneratorService.DescribeTableAsync(_session, _database, row.Table, cancellationToken);
                _described[row.Table] = info;
            }

            if (!_rules.TryGetValue(row.Table, out var rules))
            {
                rules = new Dictionary<string, DataGeneratorRule>(StringComparer.OrdinalIgnoreCase);
                _rules[row.Table] = rules;
            }

            _columnHeader.Text = $"{row.Table.DisplayName} 的欄位規則";
            _columnPanel.Children.Clear();
            foreach (var note in info.Notes)
            {
                _columnPanel.Children.Add(new TextBlock { Text = note, FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#B54708")), TextWrapping = TextWrapping.Wrap });
            }

            foreach (var column in info.Columns)
            {
                _columnPanel.Children.Add(BuildColumnEditor(column, rules));
            }

            _status.Text = $"{row.Table.DisplayName}：{info.Columns.Count} 個欄位。";
        });
    }

    private Control BuildColumnEditor(DataGeneratorColumnInfo info, Dictionary<string, DataGeneratorRule> rules)
    {
        var column = info.Column;
        var current = rules.TryGetValue(column.Name, out var saved) ? saved : DataGeneratorRule.Auto;
        var computed = column.IsGenerated && !column.IsIdentity;
        var kind = new ComboBox
        {
            ItemsSource = RuleChoices.Select(choice => choice.Label).ToList(),
            SelectedIndex = Array.FindIndex(RuleChoices, choice => choice.Kind == current.Kind),
            Width = 120,
            IsEnabled = !computed
        };
        var parameter = new TextBox { Text = current.Text, MinWidth = 260 };
        var nullPercent = new NumericUpDown
        {
            Minimum = 0,
            Maximum = 100,
            Increment = 10,
            Value = current.NullPercent,
            FormatString = "0",
            Width = 110,
            IsEnabled = column.IsNullable && !info.IsUnique && !column.IsPrimaryKey && !computed
        };
        ToolTip.SetTip(nullPercent, "NULL 比例（%）");

        void Refresh()
        {
            var choice = RuleChoices[Math.Max(0, kind.SelectedIndex)];
            parameter.IsVisible = choice.Hint.Length > 0;
            parameter.PlaceholderText = choice.Hint;
        }

        void Save()
        {
            var choice = RuleChoices[Math.Max(0, kind.SelectedIndex)];
            var rule = new DataGeneratorRule(choice.Kind, parameter.Text ?? string.Empty, (int)(nullPercent.Value ?? 0));
            if (rule == DataGeneratorRule.Auto)
            {
                rules.Remove(column.Name);
            }
            else
            {
                rules[column.Name] = rule;
            }
        }

        kind.SelectionChanged += (_, _) =>
        {
            Refresh();
            Save();
        };
        parameter.TextChanged += (_, _) => Save();
        nullPercent.ValueChanged += (_, _) => Save();
        Refresh();

        var flags = new List<string> { info.DataType };
        if (column.IsPrimaryKey)
        {
            flags.Add("PK");
        }

        if (info.IsUnique && !column.IsPrimaryKey)
        {
            flags.Add("UNIQUE");
        }

        if (info.IsForeignKey)
        {
            flags.Add("FK");
        }

        flags.Add(column.IsNullable ? "NULL" : "NOT NULL");
        var title = new StackPanel { Width = 230, Spacing = 1 };
        title.Children.Add(new TextBlock { Text = column.Name, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        title.Children.Add(new TextBlock { Text = string.Join(" · ", flags), FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#667085")), TextTrimming = TextTrimming.CharacterEllipsis });

        var editors = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        editors.Children.Add(kind);
        editors.Children.Add(nullPercent);
        editors.Children.Add(parameter);
        var right = new StackPanel { Spacing = 3 };
        right.Children.Add(editors);
        right.Children.Add(new TextBlock
        {
            Text = "自動：" + info.AutoDescription,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.Parse(computed ? "#98A2B3" : "#475467"))
        });

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        grid.Children.Add(title);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        return new Border
        {
            BorderBrush = new SolidColorBrush(Color.Parse("#E4E7EC")),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(2, 6),
            Child = grid
        };
    }

    /// <summary>使用者字典的資料夾（與 Windows 版格式相同）。</summary>
    public string DictionaryDirectory { get; init; } = DataGeneratorDictionaryStore.DefaultDirectory;

    /// <summary>字典規則在產生前才讀取字典內容，編輯字典後不必重設規則。</summary>
    private Dictionary<string, DataGeneratorRule> ResolveDictionaries(Dictionary<string, DataGeneratorRule> rules) =>
        rules.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Kind == DataGeneratorRuleKind.Dictionary
                ? pair.Value with { Dictionary = DataGeneratorDictionaryStore.Load(DictionaryDirectory, pair.Value.Text) }
                : pair.Value,
            StringComparer.OrdinalIgnoreCase);

    private async Task<DataGenerationResult?> GenerateAsync(CancellationToken cancellationToken)
    {
        var selected = _rows.Where(row => row.Check.IsChecked == true).ToList();
        if (selected.Count == 0)
        {
            _status.Text = "請先勾選要產生資料的資料表。";
            return null;
        }

        int? seed = null;
        if (!string.IsNullOrWhiteSpace(_seed.Text))
        {
            if (!int.TryParse(_seed.Text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
            {
                _status.Text = "Seed 必須是整數，或留空使用隨機值。";
                return null;
            }

            seed = value;
        }

        var plans = selected.Select(row => new DataGeneratorTablePlan(
                row.Table,
                row.RowCount,
                _rules.TryGetValue(row.Table, out var rules) ? ResolveDictionaries(rules) : new Dictionary<string, DataGeneratorRule>()))
            .ToList();
        var result = await DataGeneratorService.GenerateAsync(_session, _database, plans, seed, cancellationToken);
        if (!result.Succeeded)
        {
            SetError($"無法產生：{result.Error}");
            return null;
        }

        return result;
    }

    private async Task PreviewAsync()
    {
        DataGenerationResult? result = null;
        await RunAsync("正在產生資料（尚未寫入）…", async cancellationToken =>
        {
            result = await GenerateAsync(cancellationToken);
            if (result is not null)
            {
                _status.Text = Describe(result, "已產生（尚未寫入）");
            }
        });

        if (result is not null)
        {
            var text = "-- 預覽用的 SQL（實際寫入以參數化語句在單一交易執行，不會執行這段文字）\n" +
                       string.Concat(result.Warnings.Select(warning => "-- 注意：" + warning.ReplaceLineEndings(" ") + "\n")) + "\n" +
                       DataGeneratorService.BuildPreviewSql(_session.Profile.Provider, result);
            await SqlPreviewWindow.ShowAsync(this, "資料產生 SQL 預覽", text);
        }
    }

    private async Task WriteAsync()
    {
        DataGenerationResult? result = null;
        await RunAsync("正在產生資料…", async cancellationToken => result = await GenerateAsync(cancellationToken));
        if (result is null)
        {
            return;
        }

        _status.Text = Describe(result, "已產生（等待確認，尚未寫入）");

        var summary = $"即將在「{_database}」寫入 {result.TotalRows:N0} 列：" +
                      string.Join("、", result.Tables.Select(table => $"{table.Table.DisplayName} {table.Rows.Count:N0}")) +
                      "。全部在單一交易中執行，任何一列失敗就整批回滾。" +
                      (result.Warnings.Count > 0 ? "\n\n注意：\n" + string.Join("\n", result.Warnings) : string.Empty);
        if (!await MessageDialog.ShowAsync(this, "寫入產生的資料", summary + "\n\n確定寫入？", showCancel: true, confirmText: "寫入"))
        {
            _status.Text = "已取消，沒有寫入任何資料。";
            return;
        }

        await RunAsync($"正在寫入 {result.TotalRows:N0} 列…", async cancellationToken =>
        {
            var applied = await DataGeneratorService.ApplyAsync(_session, _database, result, cancellationToken);
            if (applied.Succeeded)
            {
                _status.Text = Describe(result, "已寫入");
                _status.Foreground = new SolidColorBrush(Color.Parse("#067647"));
            }
            else
            {
                SetError($"寫入失敗，交易已回滾、資料沒有任何變更：{applied.FailedTable} {applied.Message}");
            }
        });
    }

    private static string Describe(DataGenerationResult result, string verb) =>
        $"{verb} {result.TotalRows:N0} 列（{string.Join("、", result.Tables.Select(table => $"{table.Table.DisplayName} {table.Rows.Count:N0}"))}）" +
        (result.Warnings.Count > 0 ? $"；{result.Warnings.Count} 則注意事項見預覽。" : "。");

    private void SetError(string message)
    {
        _status.Text = message;
        _status.Foreground = new SolidColorBrush(Color.Parse("#B42318"));
    }

    private async Task RunAsync(string status, Func<CancellationToken, Task> operation)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        _status.Foreground = new SolidColorBrush(Color.Parse("#344054"));
        _status.Text = status;
        _previewButton.IsEnabled = _writeButton.IsEnabled = false;
        try
        {
            await operation(_closing.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            SetError($"操作失敗：{exception.Message}");
        }
        finally
        {
            _busy = false;
            _previewButton.IsEnabled = _writeButton.IsEnabled = _rows.Count > 0;
        }
    }

    private sealed class TableRow
    {
        private readonly Border _border;
        private readonly NumericUpDown _count = new()
        {
            Minimum = 1,
            Maximum = DataGeneratorService.MaximumRowsPerTable,
            Increment = 10,
            Value = 100,
            FormatString = "0",
            Width = 120
        };

        public TableRow(DatabaseObjectInfo table)
        {
            Table = table;
            Check = new CheckBox { IsChecked = false, VerticalAlignment = VerticalAlignment.Center };
            var name = new Button
            {
                Content = table.DisplayName,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(4, 2),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left
            };
            ToolTip.SetTip(name, "設定欄位規則");
            name.Click += (_, _) => Selected?.Invoke();
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            grid.Children.Add(Check);
            Grid.SetColumn(name, 1);
            grid.Children.Add(name);
            Grid.SetColumn(_count, 2);
            grid.Children.Add(_count);
            _border = new Border
            {
                BorderBrush = new SolidColorBrush(Color.Parse("#E4E7EC")),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(2, 3),
                Child = grid
            };
        }

        public event Func<Task>? Selected;

        public DatabaseObjectInfo Table { get; }

        public CheckBox Check { get; }

        public Control View => _border;

        public int RowCount => (int)Math.Clamp(_count.Value ?? 1, 1, DataGeneratorService.MaximumRowsPerTable);

        public void SetFocused(bool focused) =>
            _border.Background = focused ? new SolidColorBrush(Color.Parse("#EEF4FF")) : Brushes.Transparent;
    }
}
