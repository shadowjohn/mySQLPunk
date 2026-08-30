using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
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
    private readonly ComboBox _sortColumnCombo;
    private readonly ComboBox _sortDirectionCombo;
    private readonly ComboBox _exportFormatCombo;
    private readonly ComboBox _filterColumnCombo;
    private readonly ComboBox _filterOperatorCombo;
    private readonly TextBox _filterValueBox;
    private readonly Button _previousButton;
    private readonly Button _nextButton;
    private readonly Button _addButton;
    private readonly Button _editButton;
    private readonly Button _deleteButton;
    private readonly Button _refreshButton;
    private readonly Button _columnVisibilityButton;
    private readonly Button _exportPageButton;
    private readonly Button _applyFilterButton;
    private readonly Button _clearFilterButton;
    private readonly Button _closeButton;
    private TableDataSnapshot? _snapshot;
    private CancellationTokenSource? _cancellation;
    private bool _busy;
    private bool _updatingSortControls;
    private bool _updatingFilterControls;
    private bool _hasSortableColumns;
    private int _rowOffset;
    private TableDataSort? _sort;
    private TableDataFilter? _filter;
    private readonly HashSet<string> _hiddenColumnNames = new(StringComparer.Ordinal);
    private TableColumnPreferenceStore _columnPreferenceStore = new();
    private TableColumnPreferenceKey? _columnPreferenceKey;
    private string? _columnPreferenceWarning;

    public TableDataEditorWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _dataGrid = this.FindControl<DataGrid>("DataGrid")!;
        _titleText = this.FindControl<TextBlock>("TitleText")!;
        _schemaText = this.FindControl<TextBlock>("SchemaText")!;
        _statusText = this.FindControl<TextBlock>("StatusText")!;
        _pageText = this.FindControl<TextBlock>("PageText")!;
        _sortColumnCombo = this.FindControl<ComboBox>("SortColumnCombo")!;
        _sortDirectionCombo = this.FindControl<ComboBox>("SortDirectionCombo")!;
        _exportFormatCombo = this.FindControl<ComboBox>("ExportFormatCombo")!;
        _filterColumnCombo = this.FindControl<ComboBox>("FilterColumnCombo")!;
        _filterOperatorCombo = this.FindControl<ComboBox>("FilterOperatorCombo")!;
        _filterValueBox = this.FindControl<TextBox>("FilterValueBox")!;
        _previousButton = this.FindControl<Button>("PreviousButton")!;
        _nextButton = this.FindControl<Button>("NextButton")!;
        _addButton = this.FindControl<Button>("AddButton")!;
        _editButton = this.FindControl<Button>("EditButton")!;
        _deleteButton = this.FindControl<Button>("DeleteButton")!;
        _refreshButton = this.FindControl<Button>("RefreshButton")!;
        _columnVisibilityButton = this.FindControl<Button>("ColumnVisibilityButton")!;
        _exportPageButton = this.FindControl<Button>("ExportPageButton")!;
        _applyFilterButton = this.FindControl<Button>("ApplyFilterButton")!;
        _clearFilterButton = this.FindControl<Button>("ClearFilterButton")!;
        _closeButton = this.FindControl<Button>("CloseButton")!;
        _updatingSortControls = true;
        _sortDirectionCombo.ItemsSource = new[] { "遞增（A → Z）", "遞減（Z → A）" };
        _sortDirectionCombo.SelectedIndex = 0;
        _updatingSortControls = false;
        _updatingFilterControls = true;
        _filterOperatorCombo.ItemsSource = new[]
        {
            new FilterOperatorOption("精確等於", TableDataFilterOperator.Equals),
            new FilterOperatorOption("是 NULL", TableDataFilterOperator.IsNull),
            new FilterOperatorOption("不是 NULL", TableDataFilterOperator.IsNotNull)
        };
        _filterOperatorCombo.SelectedIndex = 0;
        _updatingFilterControls = false;
    }

    public TableDataEditorWindow(
        IDatabaseSession session,
        string database,
        DatabaseObjectInfo table,
        TableColumnPreferenceStore? columnPreferenceStore = null)
        : this()
    {
        _session = session;
        _database = database;
        _table = table;
        _columnPreferenceStore = columnPreferenceStore ?? _columnPreferenceStore;
        _columnPreferenceKey = new TableColumnPreferenceKey(
            session.Profile.Id,
            database,
            table.Schema,
            table.Name);

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
        await LoadColumnPreferencesAsync();
        await LoadDataAsync();
    }

    private async Task LoadColumnPreferencesAsync()
    {
        if (_columnPreferenceKey is null)
        {
            return;
        }

        try
        {
            var hidden = await _columnPreferenceStore.LoadHiddenColumnsAsync(_columnPreferenceKey);
            _hiddenColumnNames.Clear();
            _hiddenColumnNames.UnionWith(hidden);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            _columnPreferenceWarning = $"無法載入欄位偏好：{exception.Message} 已改為顯示全部欄位。";
            _hiddenColumnNames.Clear();
        }
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

    private async void ExportPage_Click(object? sender, RoutedEventArgs e)
    {
        var snapshot = _snapshot;
        if (snapshot is null)
        {
            return;
        }

        if (snapshot.WasTruncated || snapshot.RowOffset > 0)
        {
            var confirmed = await MessageDialog.ShowAsync(
                this,
                "只匯出目前頁面",
                $"只會匯出第 {(_rowOffset / RowLimit) + 1:N0} 頁目前載入的 {snapshot.Rows.Count:N0} 列，" +
                "並保留目前排序；其他頁不會包含。是否繼續？",
                showCancel: true);
            if (!confirmed)
            {
                return;
            }
        }

        if (!StorageProvider.CanSave)
        {
            await MessageDialog.ShowAsync(
                this,
                "無法匯出目前頁面",
                "目前桌面環境未提供儲存檔案對話框。",
                showCancel: false);
            return;
        }

        var selectedFormat = _exportFormatCombo.SelectedIndex switch
        {
            1 => QueryResultExportFormat.Tsv,
            2 => QueryResultExportFormat.Json,
            _ => QueryResultExportFormat.Csv
        };
        var extension = QueryResultExportService.GetDefaultExtension(selectedFormat);
        var fileType = new FilePickerFileType(
            $"{QueryResultExportService.GetFormatDisplayName(selectedFormat)} Table 頁面")
        {
            Patterns = new[] { $"*.{extension}" },
            MimeTypes = selectedFormat switch
            {
                QueryResultExportFormat.Csv => new[] { "text/csv" },
                QueryResultExportFormat.Tsv => new[] { "text/tab-separated-values" },
                QueryResultExportFormat.Json => new[] { "application/json" },
                _ => Array.Empty<string>()
            }
        };
        IStorageFile? file;
        try
        {
            file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "匯出目前 Table 頁面",
                SuggestedFileName = BuildSuggestedExportFileName(extension),
                DefaultExtension = extension,
                FileTypeChoices = new[] { fileType }
            });
        }
        catch (Exception exception)
        {
            await MessageDialog.ShowAsync(
                this,
                "無法開啟儲存檔案對話框",
                exception.Message,
                showCancel: false);
            return;
        }

        if (file is null)
        {
            return;
        }

        if (file.TryGetLocalPath() is not { } path)
        {
            await MessageDialog.ShowAsync(
                this,
                "無法匯出目前頁面",
                "目前只能匯出到本機檔案。",
                showCancel: false);
            return;
        }

        var visibleColumnNames = GetVisibleColumns(snapshot)
            .Select(column => column.Name)
            .ToHashSet(StringComparer.Ordinal);
        var result = QueryResultExportService.CreateTablePageResult(snapshot, visibleColumnNames);
        var format = QueryResultExportService.ResolveFormat(path, selectedFormat);
        QueryResultExportSummary? summary = null;
        var succeeded = await RunAsync("正在安全匯出目前頁面…", async cancellationToken =>
        {
            summary = await QueryResultExportService.WriteFileAsync(result, path, format, cancellationToken);
        });
        if (succeeded && summary is not null)
        {
            _statusText.Text =
                $"已匯出本頁 {summary.Rows:N0} 列 {summary.FormatDisplayName}（{summary.FormattedBytes}）：{summary.Path}";
        }
    }

    private async void ColumnVisibility_Click(object? sender, RoutedEventArgs e)
    {
        var snapshot = _snapshot;
        if (_busy || snapshot is null)
        {
            return;
        }

        var currentVisible = GetVisibleColumns(snapshot)
            .Select(column => column.Name)
            .ToHashSet(StringComparer.Ordinal);
        var dialog = new TableColumnVisibilityDialog(snapshot.Columns, currentVisible);
        var selected = await dialog.ShowDialog<IReadOnlySet<string>?>(this);
        if (selected is null)
        {
            return;
        }

        _hiddenColumnNames.Clear();
        _hiddenColumnNames.UnionWith(snapshot.Columns
            .Select(column => column.Name)
            .Where(columnName => !selected.Contains(columnName)));
        RebuildGrid(snapshot);
        UpdateColumnVisibilityButton(snapshot);
        var preferenceStatus = await SaveColumnPreferencesAsync();
        _statusText.Text =
            $"目前顯示 {selected.Count:N0}／{snapshot.Columns.Count:N0} 欄；本頁匯出只包含可見欄位。{preferenceStatus}";
        UpdateActionState();
    }

    private async Task<string> SaveColumnPreferencesAsync()
    {
        if (_columnPreferenceKey is null)
        {
            return string.Empty;
        }

        try
        {
            await _columnPreferenceStore.SaveHiddenColumnsAsync(_columnPreferenceKey, _hiddenColumnNames);
            _columnPreferenceWarning = null;
            return "欄位顯示偏好已安全保存。";
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            _columnPreferenceWarning = $"無法保存欄位偏好：{exception.Message}";
            return _columnPreferenceWarning;
        }
    }

    private void FilterControl_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (!_updatingFilterControls)
        {
            UpdateActionState();
        }
    }

    private async void ApplyFilter_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy ||
            _filterColumnCombo.SelectedItem is not FilterColumnOption column ||
            _filterOperatorCombo.SelectedItem is not FilterOperatorOption filterOperator)
        {
            return;
        }

        var nextFilter = new TableDataFilter(
            column.ColumnName,
            filterOperator.Operator,
            filterOperator.Operator == TableDataFilterOperator.Equals ? _filterValueBox.Text ?? string.Empty : string.Empty);
        if (nextFilter == _filter)
        {
            return;
        }

        var previousFilter = _filter;
        var previousOffset = _rowOffset;
        _filter = nextFilter;
        _rowOffset = 0;
        if (!await LoadDataAsync())
        {
            _filter = previousFilter;
            _rowOffset = previousOffset;
            UpdateFilterControls(_snapshot);
            UpdateActionState();
        }
    }

    private async void ClearFilter_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy || _filter is null)
        {
            return;
        }

        var previousFilter = _filter;
        var previousOffset = _rowOffset;
        _filter = null;
        _rowOffset = 0;
        if (!await LoadDataAsync())
        {
            _filter = previousFilter;
            _rowOffset = previousOffset;
            UpdateFilterControls(_snapshot);
            UpdateActionState();
        }
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

    private string BuildSuggestedExportFileName(string extension)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var safeTableName = new string(_table.DisplayName
            .Select(character => invalidCharacters.Contains(character) || char.IsControl(character) ? '-' : character)
            .Take(80)
            .ToArray())
            .Trim(' ', '.', '-');
        if (safeTableName.Length == 0)
        {
            safeTableName = "table";
        }

        return $"mysqlpunk-{safeTableName}-page-{(_rowOffset / RowLimit) + 1}-{DateTime.Now:yyyyMMdd-HHmmss}.{extension}";
    }

    private void DataGrid_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateActionState();
    }

    private async void Sort_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingSortControls || _busy || _snapshot is null ||
            _sortColumnCombo.SelectedItem is not SortColumnOption selectedOption)
        {
            return;
        }

        var nextSort = selectedOption.ColumnName is null
            ? null
            : new TableDataSort(selectedOption.ColumnName, _sortDirectionCombo.SelectedIndex == 1);
        if (nextSort == _sort)
        {
            return;
        }

        var previousSort = _sort;
        var previousOffset = _rowOffset;
        _sort = nextSort;
        _rowOffset = 0;
        if (!await LoadDataAsync())
        {
            _sort = previousSort;
            _rowOffset = previousOffset;
            UpdateSortControls(_snapshot);
            UpdateActionState();
        }
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
                cancellationToken,
                _sort,
                _filter);
        });
        if (!succeeded || loadedSnapshot is null)
        {
            return false;
        }

        _snapshot = loadedSnapshot;
        UpdateSortControls(loadedSnapshot);
        UpdateFilterControls(loadedSnapshot);
        NormalizeHiddenColumns(loadedSnapshot);
        RebuildGrid(loadedSnapshot);
        UpdateColumnVisibilityButton(loadedSnapshot);
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
        var sortStatus = !loadedSnapshot.HasPrimaryKey
            ? "沒有 Primary Key，不提供欄位排序。"
            : _sort is null
                ? "依 Primary Key 預設順序。"
                : $"依 {_sort.ColumnName} {(_sort.Descending ? "遞減" : "遞增")}排序，相同值再依 Primary Key 遞增排序。";
        var filterStatus = _filter is null
            ? "未套用篩選。"
            : $"已套用 {_filter.ColumnName} {GetFilterOperatorDisplayName(_filter.Operator)}篩選。";
        var columnStatus = $"顯示 {GetVisibleColumns(loadedSnapshot).Count:N0}／{loadedSnapshot.Columns.Count:N0} 欄。";
        _statusText.Text = string.Join(
            " ",
            new[] { successPrefix, range, keyStatus, pagingStatus, sortStatus, filterStatus, columnStatus, _columnPreferenceWarning }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        UpdateActionState();
        return true;
    }

    private void RebuildGrid(TableDataSnapshot snapshot)
    {
        _dataGrid.Columns.Clear();
        foreach (var column in GetVisibleColumns(snapshot))
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

    private IReadOnlyList<TableColumnInfo> GetVisibleColumns(TableDataSnapshot snapshot)
    {
        var columns = snapshot.Columns
            .Where(column => !_hiddenColumnNames.Contains(column.Name))
            .OrderBy(column => column.Ordinal)
            .ToList();
        if (columns.Count == 0)
        {
            throw new InvalidOperationException("Table 至少需要一個顯示欄位。");
        }

        return columns;
    }

    private void NormalizeHiddenColumns(TableDataSnapshot snapshot)
    {
        var currentColumnNames = snapshot.Columns
            .Select(column => column.Name)
            .ToHashSet(StringComparer.Ordinal);
        _hiddenColumnNames.IntersectWith(currentColumnNames);
        if (_hiddenColumnNames.Count >= snapshot.Columns.Count)
        {
            _hiddenColumnNames.Clear();
            _columnPreferenceWarning = "既有欄位偏好與目前 schema 不相容，已安全改為顯示全部欄位。";
        }
    }

    private void UpdateColumnVisibilityButton(TableDataSnapshot snapshot)
    {
        var visibleCount = snapshot.Columns.Count(column => !_hiddenColumnNames.Contains(column.Name));
        _columnVisibilityButton.Content = $"欄位 {visibleCount:N0}/{snapshot.Columns.Count:N0}…";
    }

    private void UpdateSortControls(TableDataSnapshot snapshot)
    {
        _updatingSortControls = true;
        try
        {
            var options = new List<SortColumnOption>
            {
                new("Primary Key 預設順序", null)
            };
            if (snapshot.HasPrimaryKey)
            {
                options.AddRange(snapshot.Columns
                    .Where(column => TableDataSortService.IsSortable(_session.Profile.Provider, column))
                    .OrderBy(column => column.Ordinal)
                    .Select(column => new SortColumnOption(
                        $"{column.Name} · {column.DataTypeName}",
                        column.Name)));
            }

            _hasSortableColumns = options.Count > 1;
            _sortColumnCombo.ItemsSource = options;
            var selectedIndex = _sort is null
                ? 0
                : options.FindIndex(option =>
                    string.Equals(option.ColumnName, _sort.ColumnName, StringComparison.Ordinal));
            _sortColumnCombo.SelectedIndex = selectedIndex < 0 ? 0 : selectedIndex;
            _sortDirectionCombo.SelectedIndex = _sort?.Descending == true ? 1 : 0;
        }
        finally
        {
            _updatingSortControls = false;
        }
    }

    private void UpdateFilterControls(TableDataSnapshot? snapshot)
    {
        _updatingFilterControls = true;
        try
        {
            var options = snapshot?.Columns
                .Where(column => TableDataFilterService.IsFilterable(_session.Profile.Provider, column))
                .OrderBy(column => column.Ordinal)
                .Select(column => new FilterColumnOption(
                    $"{column.Name} · {column.DataTypeName}",
                    column.Name))
                .ToList() ?? new List<FilterColumnOption>();
            _filterColumnCombo.ItemsSource = options;
            var selectedColumnIndex = _filter is null
                ? 0
                : options.FindIndex(option =>
                    string.Equals(option.ColumnName, _filter.ColumnName, StringComparison.Ordinal));
            _filterColumnCombo.SelectedIndex = options.Count == 0 ? -1 : Math.Max(0, selectedColumnIndex);
            _filterOperatorCombo.SelectedIndex = _filter?.Operator switch
            {
                TableDataFilterOperator.IsNull => 1,
                TableDataFilterOperator.IsNotNull => 2,
                _ => 0
            };
            _filterValueBox.Text = _filter?.Operator == TableDataFilterOperator.Equals
                ? _filter.Value
                : string.Empty;
        }
        finally
        {
            _updatingFilterControls = false;
        }
    }

    private static string GetFilterOperatorDisplayName(TableDataFilterOperator filterOperator) => filterOperator switch
    {
        TableDataFilterOperator.Equals => "精確等於",
        TableDataFilterOperator.IsNull => "是 NULL",
        TableDataFilterOperator.IsNotNull => "不是 NULL",
        _ => throw new ArgumentOutOfRangeException(nameof(filterOperator))
    };

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
            var message = FormatMutationError(exception);
            _statusText.Text = $"{title}：{message}";
            await MessageDialog.ShowAsync(this, title, message, showCancel: false);
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

    private static string FormatMutationError(Exception exception) =>
        exception.Message.Contains("mysqlpunk-string-invalid-", StringComparison.Ordinal)
            ? "SQL Server 無法將輸入文字依欄位 collation 無損保存，或輸入超過欄位的 byte 上限；本次修改已回復，資料未變更。"
            : exception.Message;

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
        _columnVisibilityButton.IsEnabled = !_busy && hasSnapshot;
        _exportFormatCombo.IsEnabled = !_busy && hasSnapshot;
        _exportPageButton.IsEnabled = !_busy && hasSnapshot;
        _closeButton.IsEnabled = !_busy;
        _dataGrid.IsEnabled = !_busy;
        _sortColumnCombo.IsEnabled = !_busy && hasSnapshot && _snapshot!.HasPrimaryKey && _hasSortableColumns;
        _sortDirectionCombo.IsEnabled = _sortColumnCombo.IsEnabled && _sort is not null;
        var hasFilterColumn = _filterColumnCombo.SelectedItem is FilterColumnOption;
        var filterNeedsValue = _filterOperatorCombo.SelectedItem is FilterOperatorOption
        {
            Operator: TableDataFilterOperator.Equals
        };
        _filterColumnCombo.IsEnabled = !_busy && hasSnapshot && _filterColumnCombo.ItemCount > 0;
        _filterOperatorCombo.IsEnabled = !_busy && hasSnapshot && hasFilterColumn;
        _filterValueBox.IsEnabled = !_busy && hasSnapshot && hasFilterColumn && filterNeedsValue;
        _applyFilterButton.IsEnabled = !_busy && hasSnapshot && hasFilterColumn;
        _clearFilterButton.IsEnabled = !_busy && _filter is not null;
    }

    private sealed record TableDataRowView(TableDataRow Source, IReadOnlyList<string> Values);

    private sealed record SortColumnOption(string DisplayName, string? ColumnName)
    {
        public override string ToString() => DisplayName;
    }

    private sealed record FilterColumnOption(string DisplayName, string ColumnName)
    {
        public override string ToString() => DisplayName;
    }

    private sealed record FilterOperatorOption(string DisplayName, TableDataFilterOperator Operator)
    {
        public override string ToString() => DisplayName;
    }
}
