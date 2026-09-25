using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MySqlPunk.Core.Models;
using MySqlPunk.Core.Providers;
using MySqlPunk.Core.Services;

namespace MySqlPunk.Desktop;

/// <summary>
/// Row-level data comparison between two databases of the same provider, with an optional synchronization of the
/// target. Comparison only reads. Synchronization runs in one transaction on the target session, requires a
/// confirmation, and requires typing the target database name when deletes are included.
/// </summary>
internal sealed class DataCompareWindow : Window
{
    private readonly IDatabaseSession _source;
    private readonly string _sourceDatabase;
    private readonly IDatabaseSession _target;
    private readonly string _targetDatabase;
    private readonly StackPanel _tablePanel = new() { Spacing = 2 };
    private readonly List<TableRow> _rows = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly CheckBox _includeDeletes = new() { Content = "包含刪除只在目標的資料列（破壞性，預設不勾）" };
    private readonly Button _compareButton = new() { Content = "比較勾選的資料表", Padding = new Thickness(14, 6) };
    private readonly Button _previewButton = new() { Content = "預覽 SQL…", IsEnabled = false };
    private readonly Button _syncButton = new() { Content = "在目標同步…", IsEnabled = false, Padding = new Thickness(14, 6) };
    private readonly CancellationTokenSource _closing = new();
    private IReadOnlyList<DatabaseObjectInfo> _order = Array.Empty<DatabaseObjectInfo>();
    private bool _busy;

    public DataCompareWindow(IDatabaseSession source, string sourceDatabase, IDatabaseSession target, string targetDatabase)
    {
        _source = source;
        _sourceDatabase = sourceDatabase;
        _target = target;
        _targetDatabase = targetDatabase;
        Title = "資料比較";
        Width = 1040;
        Height = 720;
        MinWidth = 700;
        MinHeight = 460;
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
                    new TextBlock { Text = "資料比較與同步", Foreground = Brushes.White, FontSize = 17, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = $"來源：{source.Profile.Name}／{sourceDatabase}", Foreground = new SolidColorBrush(Color.Parse("#B9C7E8")), FontSize = 12 },
                    new TextBlock { Text = $"目標：{target.Profile.Name}／{targetDatabase}（同步只會修改這一邊）", Foreground = new SolidColorBrush(Color.Parse("#FEC84B")), FontSize = 12 }
                }
            }
        };

        var notice = new TextBlock
        {
            Text = "依 Primary Key 逐列比對兩邊都有的資料表與欄位；比較只讀取資料，每表上限 " +
                   $"{DataComparisonService.DefaultMaximumRows:N0} 列。同步以單一交易套用，比較後目標若被修改會整批回滾。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#475467"))
        };

        var selectAll = new Button { Content = "全選", Margin = new Thickness(0, 0, 8, 0) };
        selectAll.Click += (_, _) => _rows.ForEach(row => row.Check.IsChecked = true);
        var selectNone = new Button { Content = "全不選", Margin = new Thickness(0, 0, 8, 0) };
        selectNone.Click += (_, _) => _rows.ForEach(row => row.Check.IsChecked = false);
        _compareButton.Click += async (_, _) => await CompareAsync();
        _previewButton.Click += async (_, _) => await PreviewAsync();
        _syncButton.Click += async (_, _) => await SyncAsync();
        var close = new Button { Content = "關閉", Padding = new Thickness(14, 6), Margin = new Thickness(8, 0, 0, 0) };
        close.Click += (_, _) => Close();

        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 8) };
        toolbar.Children.Add(selectAll);
        toolbar.Children.Add(selectNone);
        toolbar.Children.Add(_compareButton);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(_previewButton);
        actions.Children.Add(new Border { Width = 8 });
        actions.Children.Add(_syncButton);
        actions.Children.Add(close);

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 8, 0, 0) };
        var left = new StackPanel { Spacing = 6 };
        left.Children.Add(_includeDeletes);
        left.Children.Add(_status);
        footer.Children.Add(left);
        Grid.SetColumn(actions, 1);
        footer.Children.Add(actions);

        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), Margin = new Thickness(14, 10, 14, 12) };
        body.Children.Add(notice);
        Grid.SetRow(toolbar, 1);
        body.Children.Add(toolbar);
        var scroller = new ScrollViewer { Content = _tablePanel };
        Grid.SetRow(scroller, 2);
        body.Children.Add(scroller);
        Grid.SetRow(footer, 3);
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
        await RunAsync("正在讀取兩邊的資料表…", async cancellationToken =>
        {
            if (_source.Profile.Provider != _target.Profile.Provider)
            {
                _status.Text = "來源與目標是不同類型的資料庫；資料比較與同步只支援同類型。";
                _compareButton.IsEnabled = false;
                return;
            }

            var sourceTables = (await _source.GetObjectsAsync(_sourceDatabase, cancellationToken))
                .Where(item => item.Kind == DatabaseObjectKind.Table).ToList();
            var targetTables = (await _target.GetObjectsAsync(_targetDatabase, cancellationToken))
                .Where(item => item.Kind == DatabaseObjectKind.Table).ToList();
            foreach (var sourceTable in sourceTables.OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                var targetTable = targetTables.FirstOrDefault(item =>
                                      item.Schema.Equals(sourceTable.Schema, StringComparison.OrdinalIgnoreCase) &&
                                      item.Name.Equals(sourceTable.Name, StringComparison.OrdinalIgnoreCase)) ??
                                  targetTables.FirstOrDefault(item => item.Name.Equals(sourceTable.Name, StringComparison.OrdinalIgnoreCase));
                if (targetTable is null)
                {
                    continue;
                }

                var row = new TableRow(sourceTable, targetTable);
                _rows.Add(row);
                _tablePanel.Children.Add(row.View);
            }

            _status.Text = _rows.Count == 0
                ? "兩邊沒有同名的資料表。"
                : $"找到 {_rows.Count} 張兩邊都有的資料表；勾選後按「比較勾選的資料表」。";
        });
    }

    private async Task CompareAsync()
    {
        var selected = _rows.Where(row => row.Check.IsChecked == true).ToList();
        if (selected.Count == 0)
        {
            _status.Text = "請先勾選要比較的資料表。";
            return;
        }

        await RunAsync("正在比較資料…", async cancellationToken =>
        {
            foreach (var row in _rows)
            {
                row.SetComparison(null);
            }

            var structures = new List<TableStructureInfo>();
            foreach (var row in selected)
            {
                structures.Add(await _target.GetTableStructureAsync(_targetDatabase, row.Target, cancellationToken));
            }

            _order = DataComparisonService.OrderByDependencies(selected.Select(row => row.Target).ToList(), structures);
            var done = 0;
            foreach (var row in selected)
            {
                _status.Text = $"比較 {++done}/{selected.Count}：{row.Target.DisplayName}";
                row.SetComparison(await DataComparisonService.CompareTableAsync(
                    _source, _sourceDatabase, row.Source, _target, _targetDatabase, row.Target, cancellationToken: cancellationToken));
            }

            var compared = selected.Select(row => row.Comparison!).ToList();
            var changes = compared.Sum(item => item.Changes.Count);
            _status.Text = changes == 0
                ? $"比較完成：{compared.Count} 張表資料一致" + (compared.Any(item => item.IsSkipped) ? "（部分資料表略過，見各表說明）。" : "。")
                : $"比較完成：新增 {compared.Sum(item => item.Inserts):N0}、修改 {compared.Sum(item => item.Updates):N0}、" +
                  $"只在目標 {compared.Sum(item => item.Deletes):N0} 列。";
        });
    }

    private List<DataTableComparison> ComparedWithChanges() => _rows
        .Where(row => row.Comparison is { IsSkipped: false } && row.Comparison.Changes.Count > 0)
        .Select(row => row.Comparison!)
        .ToList();

    private async Task PreviewAsync()
    {
        var comparisons = ComparedWithChanges();
        if (comparisons.Count == 0)
        {
            _status.Text = "沒有可預覽的差異。";
            return;
        }

        var includeDeletes = _includeDeletes.IsChecked == true;
        var text = "-- 預覽用的 SQL（實際同步以參數化語句在單一交易執行，不會執行這段文字）\n" +
                   (includeDeletes ? string.Empty : "-- 未勾選刪除：DELETE 以註解呈現\n") + "\n" +
                   string.Join("\n", comparisons.Select(item => DataComparisonService.BuildPreviewSql(_target.Profile.Provider, item, includeDeletes)));
        await SqlPreviewWindow.ShowAsync(this, "資料同步 SQL 預覽", text);
    }

    private async Task SyncAsync()
    {
        var comparisons = ComparedWithChanges();
        var includeDeletes = _includeDeletes.IsChecked == true;
        var requests = _order
            .Select(table => comparisons.FirstOrDefault(item => ReferenceEquals(item.TargetTable, table) || item.TargetTable == table))
            .Where(item => item is not null)
            .Select(item => new DataSyncTableRequest(
                item!.TargetTable,
                item.Changes.Where(change => includeDeletes || change.Kind != DataRowChangeKind.Delete).ToList()))
            .Where(request => request.Changes.Count > 0)
            .ToList();
        if (requests.Count == 0)
        {
            _status.Text = includeDeletes ? "沒有需要同步的資料列。" : "沒有需要同步的資料列（只在目標的資料列需勾選「包含刪除」）。";
            return;
        }

        var inserts = requests.Sum(request => request.Changes.Count(change => change.Kind == DataRowChangeKind.Insert));
        var updates = requests.Sum(request => request.Changes.Count(change => change.Kind == DataRowChangeKind.Update));
        var deletes = requests.Sum(request => request.Changes.Count(change => change.Kind == DataRowChangeKind.Delete));
        var summary = $"即將在目標資料庫「{_targetDatabase}」的 {requests.Count} 張表新增 {inserts:N0}、修改 {updates:N0}、刪除 {deletes:N0} 列（單一交易）。";
        var confirmed = deletes > 0
            ? await TypedConfirmation.ShowAsync(this, summary + "\n刪除的資料無法復原；請先確認已備份，並輸入目標資料庫名稱以繼續：", _targetDatabase)
            : await MessageDialog.ShowAsync(this, "在目標同步資料", summary + " 確定繼續？", showCancel: true, confirmText: "同步");
        if (!confirmed)
        {
            return;
        }

        DataSyncResult? result = null;
        await RunAsync("正在目標同步資料…", async cancellationToken =>
        {
            result = await _target.ApplyDataSyncAsync(_targetDatabase, requests, cancellationToken);
            _status.Text = result.Summary;
            _status.Foreground = new SolidColorBrush(Color.Parse(result.Succeeded ? "#067647" : "#B42318"));
        });

        if (result is { Succeeded: true })
        {
            var message = result.Summary;
            await CompareAsync();
            _status.Text = $"{message} 已重新比較：{_status.Text}";
        }
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
        UpdateButtons();
        try
        {
            await operation(_closing.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _status.Text = $"操作失敗：{exception.Message}";
            _status.Foreground = new SolidColorBrush(Color.Parse("#B42318"));
        }
        finally
        {
            _busy = false;
            UpdateButtons();
        }
    }

    private void UpdateButtons()
    {
        var hasChanges = ComparedWithChanges().Count > 0;
        _compareButton.IsEnabled = !_busy && _rows.Count > 0;
        _previewButton.IsEnabled = !_busy && hasChanges;
        _syncButton.IsEnabled = !_busy && hasChanges;
    }

    private sealed class TableRow
    {
        private readonly TextBlock _status = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#667085")) };
        private readonly TextBlock _warnings = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#B54708")) };

        public TableRow(DatabaseObjectInfo source, DatabaseObjectInfo target)
        {
            Source = source;
            Target = target;
            Check = new CheckBox { IsChecked = true, VerticalAlignment = VerticalAlignment.Top };
            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(new TextBlock { Text = target.DisplayName, FontWeight = FontWeight.SemiBold });
            text.Children.Add(_status);
            text.Children.Add(_warnings);
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

        public DatabaseObjectInfo Source { get; }

        public DatabaseObjectInfo Target { get; }

        public CheckBox Check { get; }

        public Control View { get; }

        public DataTableComparison? Comparison { get; private set; }

        public void SetComparison(DataTableComparison? comparison)
        {
            Comparison = comparison;
            _status.Text = comparison?.StatusText ?? string.Empty;
            _status.Foreground = new SolidColorBrush(Color.Parse(comparison switch
            {
                null => "#667085",
                { IsSkipped: true } => "#B54708",
                { Changes.Count: 0 } => "#067647",
                _ => "#1D4ED8"
            }));
            _warnings.Text = comparison is null || comparison.Warnings.Count == 0
                ? string.Empty
                : string.Join("\n", comparison.Warnings.Take(5)) + (comparison.Warnings.Count > 5 ? $"\n…另有 {comparison.Warnings.Count - 5} 則" : string.Empty);
        }
    }
}
