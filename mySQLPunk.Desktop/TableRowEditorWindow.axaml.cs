using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using MySqlPunk.Core.Models;
using MySqlPunk.Core.Services;

namespace MySqlPunk.Desktop;

public sealed partial class TableRowEditorWindow : Window
{
    private readonly IReadOnlyList<TableColumnInfo> _columns;
    private readonly TableDataRow? _originalRow;
    private readonly bool _isInsert;
    private readonly StackPanel _fieldsPanel;
    private readonly TextBlock _hintText;
    private readonly TextBlock _errorText;
    private readonly List<FieldEditor> _editors = new();

    public TableRowEditorWindow()
        : this(Array.Empty<TableColumnInfo>(), originalRow: null)
    {
    }

    public TableRowEditorWindow(
        IReadOnlyList<TableColumnInfo> columns,
        TableDataRow? originalRow)
    {
        AvaloniaXamlLoader.Load(this);
        _columns = columns;
        _originalRow = originalRow;
        _isInsert = originalRow is null;
        _fieldsPanel = this.FindControl<StackPanel>("FieldsPanel")!;
        _hintText = this.FindControl<TextBlock>("HintText")!;
        _errorText = this.FindControl<TextBlock>("ErrorText")!;

        Title = _isInsert ? "新增資料列" : "修改資料列";
        _hintText.Text = _isInsert
            ? "預設會交由資料庫套用欄位 DEFAULT；取消勾選「使用預設值」後即可輸入。"
            : "Primary Key、generated 與尚未支援的型別維持唯讀；只會送出實際變更的欄位。";
        BuildFields();
    }

    private void BuildFields()
    {
        foreach (var column in _columns.OrderBy(column => column.Ordinal))
        {
            var original = _originalRow?.Values[column.Ordinal];
            var readOnly = !_isInsert && column.IsPrimaryKey || !column.IsEditable;
            var label = new TextBlock
            {
                Text = BuildColumnLabel(column),
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            };
            var valueBox = new TextBox
            {
                Text = TableCellValueConverter.Format(original),
                IsReadOnly = readOnly,
                IsEnabled = !readOnly,
                PlaceholderText = BuildWatermark(column)
            };
            var nullCheck = new CheckBox
            {
                Content = "NULL",
                IsVisible = column.IsNullable && !readOnly,
                IsChecked = !_isInsert && original is null,
                VerticalAlignment = VerticalAlignment.Center
            };
            var defaultCheck = new CheckBox
            {
                Content = "使用預設值",
                IsVisible = _isInsert && column.IsEditable && (column.HasDefault || column.IsNullable),
                IsChecked = _isInsert && column.IsEditable && (column.HasDefault || column.IsNullable),
                VerticalAlignment = VerticalAlignment.Center
            };
            var field = new FieldEditor(column, valueBox, nullCheck, defaultCheck, original, readOnly);
            _editors.Add(field);

            nullCheck.IsCheckedChanged += (_, _) => ApplyFieldState(field);
            defaultCheck.IsCheckedChanged += (_, _) => ApplyFieldState(field);
            ApplyFieldState(field);

            var options = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { nullCheck, defaultCheck }
            };
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("210,*,Auto"),
                ColumnSpacing = 10
            };
            row.Children.Add(label);
            Grid.SetColumn(valueBox, 1);
            row.Children.Add(valueBox);
            Grid.SetColumn(options, 2);
            row.Children.Add(options);
            _fieldsPanel.Children.Add(row);
        }
    }

    private void ApplyFieldState(FieldEditor editor)
    {
        if (editor.ReadOnly)
        {
            editor.ValueBox.IsEnabled = false;
            return;
        }

        var useDefault = _isInsert && editor.DefaultCheck.IsChecked == true;
        var useNull = editor.NullCheck.IsChecked == true;
        editor.ValueBox.IsEnabled = !useDefault && !useNull;
        editor.NullCheck.IsEnabled = !useDefault;
        editor.DefaultCheck.IsEnabled = true;
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var values = BuildInputs();
            Close(values);
        }
        catch (Exception exception)
        {
            _errorText.Text = exception.Message;
        }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }

    private IReadOnlyList<TableCellInput> BuildInputs()
    {
        var inputs = new List<TableCellInput>();
        foreach (var editor in _editors)
        {
            if (editor.ReadOnly)
            {
                continue;
            }

            var input = editor.DefaultCheck.IsChecked == true && _isInsert
                ? new TableCellInput(editor.Column.Name, TableCellInputMode.Default, string.Empty)
                : editor.NullCheck.IsChecked == true
                    ? new TableCellInput(editor.Column.Name, TableCellInputMode.Null, string.Empty)
                    : new TableCellInput(
                        editor.Column.Name,
                        TableCellInputMode.Value,
                        editor.ValueBox.Text ?? string.Empty);

            if (!_isInsert && TableCellValueConverter.MatchesOriginal(editor.Column, input, editor.Original))
            {
                continue;
            }

            if (input.Mode != TableCellInputMode.Default)
            {
                _ = TableCellValueConverter.Parse(editor.Column, input);
            }

            inputs.Add(input);
        }

        if (!_isInsert && inputs.Count == 0)
        {
            throw new InvalidOperationException("沒有欄位內容被修改。");
        }

        return inputs;
    }

    private static string BuildColumnLabel(TableColumnInfo column)
    {
        var attributes = new List<string> { column.DataTypeName };
        if (column.IsPrimaryKey)
        {
            attributes.Add("PK");
        }

        if (column.IsGenerated)
        {
            attributes.Add("generated");
        }
        else if (!column.IsEditable)
        {
            attributes.Add("唯讀");
        }

        if (column.IsNullable)
        {
            attributes.Add("nullable");
        }

        return $"{column.Name}\n{string.Join(" · ", attributes)}";
    }

    private static string BuildWatermark(TableColumnInfo column) => column.ValueKind switch
    {
        TableColumnValueKind.Boolean => "true / false / 1 / 0",
        TableColumnValueKind.Date => "yyyy-MM-dd",
        TableColumnValueKind.DateTime => "yyyy-MM-dd HH:mm:ss",
        TableColumnValueKind.DateTimeOffset => "ISO 8601 日期時間與時區",
        TableColumnValueKind.Time => "HH:mm:ss",
        TableColumnValueKind.Guid => "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx",
        _ => string.Empty
    };

    private sealed record FieldEditor(
        TableColumnInfo Column,
        TextBox ValueBox,
        CheckBox NullCheck,
        CheckBox DefaultCheck,
        object? Original,
        bool ReadOnly);
}
