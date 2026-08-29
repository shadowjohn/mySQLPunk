using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using MySqlPunk.Core.Models;
using MySqlPunk.Core.Providers;
using MySqlPunk.Core.Services;

namespace MySqlPunk.Desktop;

public sealed partial class TableDataEditorWindow : Window
{
    private const int RowLimit = 200;

    private IDatabaseSession _session = null!;
    private string _database = string.Empty;
    private DatabaseObjectInfo _table = null!;
    private readonly DataGrid _dataGrid;
    private readonly TextBlock _titleText;
    private readonly TextBlock _schemaText;
    private readonly TextBlock _statusText;
    private readonly TextBlock _pageText;
    private readonly Button _previousButton;
    private readonly Button _nextButton;
    private readonly Button _addButton;
    private readonly Button _editButton;
    private readonly Button _deleteButton;
    private readonly Button _refreshButton;
    private readonly Button _closeButton;
    private TableDataSnapshot? _snapshot;
    private CancellationTokenSource? _cancellation;
    private bool _busy;
    private int _rowOffset;

    public TableDataEditorWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _dataGrid = this.FindControl<DataGrid>("DataGrid")!;
        _titleText = this.FindControl<TextBlock>("TitleText")!;
        _schemaText = this.FindControl<TextBlock>("SchemaText")!;
        _statusText = this.FindControl<TextBlock>("StatusText")!;
        _pageText = this.FindControl<TextBlock>("PageText")!;
        _previousButton = this.FindControl<Button>("PreviousButton")!;
        _nextButton = this.FindControl<Button>("NextButton")!;
        _addButton = this.FindControl<Button>("AddButton")!;
        _editButton = this.FindControl<Button>("EditButton")!;
        _deleteButton = this.FindControl<Button>("DeleteButton")!;
        _refreshButton = this.FindControl<Button>("RefreshButton")!;
        _closeButton = this.FindControl<Button>("CloseButton")!;
    }

    public TableDataEditorWindow(
        IDatabaseSession session,
        string database,
        DatabaseObjectInfo table)
        : this()
    {
        _session = session;
        _database = database;
        _table = table;

        Title = $"{table.DisplayName} — 資料編輯";
        _titleText.Text = table.DisplayName;
        _schemaText.Text = $"{session.Profile.ProviderDisplayName} · {database} · 每頁 {RowLimit:N0} 列";
        Opened += TableDataEditorWindow_Opened;
        Closing += (_, _) => _cancellation?.Cancel();
        UpdateActionState();
    }

    private async void TableDataEditorWindow_Opened(object? sender, EventArgs e)
    {
        Opened -= TableDataEditorWindow_Opened;
        await LoadDataAsync();
    }

    private async void Add_Click(object? sender, RoutedEventArgs e)
    {
        if (_snapshot is null)
        {
            return;
        }

        var editor = new TableRowEditorWindow(_snapshot.Columns, originalRow: null);
        var values = await editor.ShowDialog<IReadOnlyList<TableCellInput>?>(this);
        if (values is null)
        {
            return;
        }

        await RunMutationAsync(
            "正在新增資料列…",
            cancellationToken => _session.InsertTableRowAsync(_database, _table, values, cancellationToken),
            "資料列已新增。背景資料若有變動，也已在重新載入時一併更新。");
    }

    private async void Edit_Click(object? sender, RoutedEventArgs e)
    {
        await EditSelectedRowAsync();
    }

    private async void DataGrid_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_editButton.IsEnabled)
        {
            await EditSelectedRowAsync();
        }
    }

    private async Task EditSelectedRowAsync()
    {
        if (_snapshot is null || _dataGrid.SelectedItem is not TableDataRowView selected)
        {
            return;
        }

        var editor = new TableRowEditorWindow(_snapshot.Columns, selected.Source);
        var changes = await editor.ShowDialog<IReadOnlyList<TableCellInput>?>(this);
        if (changes is null)
        {
            return;
        }

        await RunMutationAsync(
            "正在安全寫入變更…",
            cancellationToken => _session.UpdateTableRowAsync(
                _database,
                _table,
                selected.Source,
                changes,
                cancellationToken),
            "資料列已修改；已用原始值確認沒有覆蓋其他連線的變更。");
    }

    private async void Delete_Click(object? sender, RoutedEventArgs e)
    {
        if (_dataGrid.SelectedItem is not TableDataRowView selected)
        {
            return;
        }

        var confirmed = await MessageDialog.ShowAsync(
            this,
            "刪除資料列",
            "確定要刪除選取的資料列嗎？寫入前會再次檢查資料是否已被其他連線修改。",
            showCancel: true);
        if (!confirmed)
        {
            return;
        }

        await RunMutationAsync(
            "正在安全刪除資料列…",
            cancellationToken => _session.DeleteTableRowAsync(
                _database,
                _table,
                selected.Source,
                cancellationToken),
            "資料列已刪除。");
    }

    private async void Refresh_Click(object? sender, RoutedEventArgs e)
    {
        await LoadDataAsync();
    }

    private async void Previous_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy || _rowOffset <= 0)
        {
            return;
        }

        var previousOffset = _rowOffset;
        _rowOffset = Math.Max(0, _rowOffset - RowLimit);
        if (!await LoadDataAsync())
        {
            _rowOffset = previousOffset;
            UpdateActionState();
        }
    }

    private async void Next_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy || _snapshot?.HasNextPage != true)
        {
            return;
        }

        var previousOffset = _rowOffset;
        _rowOffset = checked(_rowOffset + RowLimit);
        if (!await LoadDataAsync())
        {
            _rowOffset = previousOffset;
            UpdateActionState();
        }
    }

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void DataGrid_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateActionState();
    }

    private async Task RunMutationAsync(
        string busyStatus,
        Func<CancellationToken, Task> mutation,
        string successStatus)
    {
        var succeeded = await RunAsync(busyStatus, mutation);
        if (succeeded)
        {
            await LoadDataAsync(successStatus);
        }
    }

    private async Task<bool> LoadDataAsync(string? successPrefix = null)
    {
        var loadedSnapshot = default(TableDataSnapshot);
        var succeeded = await RunAsync("正在載入資料與欄位資訊…", async cancellationToken =>
        {
            loadedSnapshot = await _session.LoadTableDataAsync(
                _database,
                _table,
                RowLimit,
                _rowOffset,
                cancellationToken);
        });
        if (!succeeded || loadedSnapshot is null)
        {
            return false;
        }

        _snapshot = loadedSnapshot;
        RebuildGrid(loadedSnapshot);
        var keyStatus = loadedSnapshot.HasPrimaryKey
            ? "Primary Key 已辨識，可安全修改與刪除。"
            : "沒有 Primary Key：可新增，但修改與刪除已停用。";
        _rowOffset = loadedSnapshot.RowOffset;
        _pageText.Text = $"第 {(_rowOffset / RowLimit) + 1:N0} 頁";
        var range = loadedSnapshot.Rows.Count == 0
            ? "本頁沒有資料。"
            : $"已載入第 {_rowOffset + 1:N0}–{_rowOffset + loadedSnapshot.Rows.Count:N0} 列。";
        var pagingStatus = !loadedSnapshot.HasPrimaryKey && loadedSnapshot.WasTruncated
            ? "沒有 Primary Key，為維持穩定定位不提供後續分頁。"
            : loadedSnapshot.HasNextPage
                ? "還有下一頁。"
                : "已到最後一頁。";
        _statusText.Text = string.Join(
            " ",
            new[] { successPrefix, range, keyStatus, pagingStatus }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        UpdateActionState();
        return true;
    }

    private void RebuildGrid(TableDataSnapshot snapshot)
    {
        _dataGrid.Columns.Clear();
        foreach (var column in snapshot.Columns.OrderBy(column => column.Ordinal))
        {
            var attributes = new List<string>();
            if (column.IsPrimaryKey)
            {
                attributes.Add("PK");
            }

            if (column.IsGenerated)
            {
                attributes.Add("generated");
            }

            if (!column.IsEditable && !column.IsGenerated)
            {
                attributes.Add("唯讀");
            }

            var suffix = attributes.Count == 0 ? string.Empty : $" · {string.Join(" · ", attributes)}";
            _dataGrid.Columns.Add(new DataGridTextColumn
            {
                Header = $"{column.Name}\n{column.DataTypeName}{suffix}",
                Binding = new Binding($"Values[{column.Ordinal}]"),
                Width = new DataGridLength(1, DataGridLengthUnitType.Auto)
            });
        }

        _dataGrid.ItemsSource = snapshot.Rows
            .Select(row => new TableDataRowView(
                row,
                row.Values.Select((value, index) =>
                    TableCellValueConverter.FormatForDisplay(snapshot.Columns[index], value)).ToArray()))
            .ToList();
        _dataGrid.SelectedIndex = snapshot.Rows.Count > 0 ? 0 : -1;
    }

    private async Task<bool> RunAsync(string status, Func<CancellationToken, Task> operation)
    {
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        _busy = true;
        _statusText.Text = status;
        UpdateActionState();
        try
        {
            await operation(cancellation.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            _statusText.Text = "操作已取消。";
            return false;
        }
        catch (Exception exception)
        {
            var title = exception is TableDataConflictException ? "資料已變更" : "資料表操作失敗";
            _statusText.Text = $"{title}：{exception.Message}";
            await MessageDialog.ShowAsync(this, title, exception.Message, showCancel: false);
            return false;
        }
        finally
        {
            if (ReferenceEquals(_cancellation, cancellation))
            {
                _cancellation = null;
                cancellation.Dispose();
            }

            _busy = false;
            UpdateActionState();
        }
    }

    private void UpdateActionState()
    {
        var hasSnapshot = _snapshot is not null;
        var hasSelection = _dataGrid.SelectedItem is TableDataRowView;
        var canMutateExisting = hasSnapshot && _snapshot!.HasPrimaryKey && hasSelection;
        _addButton.IsEnabled = !_busy && hasSnapshot;
        _editButton.IsEnabled = !_busy && canMutateExisting;
        _deleteButton.IsEnabled = !_busy && canMutateExisting;
        _previousButton.IsEnabled = !_busy && _snapshot?.HasPreviousPage == true;
        _nextButton.IsEnabled = !_busy && _snapshot?.HasNextPage == true;
        _refreshButton.IsEnabled = !_busy;
        _closeButton.IsEnabled = !_busy;
        _dataGrid.IsEnabled = !_busy;
    }

    private sealed record TableDataRowView(TableDataRow Source, IReadOnlyList<string> Values);
}
