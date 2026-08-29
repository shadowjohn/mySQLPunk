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
            : "Primary Key、generated、尚未支援的型別與超過 1 MiB 的 binary／JSON／XML／bit string／PostgreSQL 文字序列化型別／spatial／exact decimal 值維持唯讀；只會送出實際變更的欄位。";
        BuildFields();
    }

    private void BuildFields()
    {
        foreach (var column in _columns.OrderBy(column => column.Ordinal))
        {
            var original = _originalRow?.Values[column.Ordinal];
            var oversizedValue = TableCellValueConverter.IsBinaryValueTooLargeToEdit(column, original) ||
                                 TableCellValueConverter.IsStructuredTextTooLargeToEdit(column, original);
            var readOnly = !_isInsert && column.IsPrimaryKey || !column.IsEditable || oversizedValue;
            var label = new TextBlock
            {
                Text = BuildColumnLabel(column, oversizedValue),
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            };
            var valueBox = new TextBox
            {
                Text = oversizedValue
                    ? TableCellValueConverter.FormatForDisplay(original)
                    : TableCellValueConverter.Format(original),
                IsReadOnly = readOnly,
                IsEnabled = !readOnly,
                PlaceholderText = BuildWatermark(column),
                AcceptsReturn = IsStructuredText(column),
                TextWrapping = IsStructuredText(column)
                    ? TextWrapping.Wrap
                    : TextWrapping.NoWrap,
                MinHeight = IsStructuredText(column) ? 90 : 0
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

    private static string BuildColumnLabel(TableColumnInfo column, bool oversizedValue)
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
        else if (oversizedValue)
        {
            attributes.Add("超過 1 MiB · 唯讀");
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
        TableColumnValueKind.String when column.DataTypeName.StartsWith("enum(", StringComparison.OrdinalIgnoreCase) =>
            "請輸入欄位宣告的 ENUM 值之一",
        TableColumnValueKind.String when column.DataTypeName.StartsWith("set(", StringComparison.OrdinalIgnoreCase) =>
            "以逗號分隔欄位宣告的 SET 值",
        TableColumnValueKind.UnsignedInteger when column.DataTypeName.StartsWith("bit(", StringComparison.OrdinalIgnoreCase) =>
            "非負十進位整數（依 BIT 寬度限制）",
        TableColumnValueKind.UnsignedInteger when column.DataTypeName.Equals("xid8", StringComparison.OrdinalIgnoreCase) =>
            $"0–{ulong.MaxValue}（十進位）",
        TableColumnValueKind.UnsignedInteger when
            column.DataTypeName.Equals("oid", StringComparison.OrdinalIgnoreCase) ||
            column.DataTypeName.Equals("xid", StringComparison.OrdinalIgnoreCase) ||
            column.DataTypeName.Equals("cid", StringComparison.OrdinalIgnoreCase) =>
            $"0–{uint.MaxValue}（十進位）",
        TableColumnValueKind.Date => "yyyy-MM-dd",
        TableColumnValueKind.DateTime => "yyyy-MM-dd HH:mm:ss",
        TableColumnValueKind.DateTimeOffset => "ISO 8601 日期時間與時區",
        TableColumnValueKind.Time => "HH:mm:ss",
        TableColumnValueKind.MySqlTime => "[-]HHH:mm:ss[.ffffff]（最大 ±838:59:59）",
        TableColumnValueKind.MySqlYear => "0 或 1901–2155（四位數年份）",
        TableColumnValueKind.ExactDecimal => BuildExactDecimalWatermark(column),
        TableColumnValueKind.TimeWithTimeZone => "HH:mm:ss.ffffff±HH:mm",
        TableColumnValueKind.Interval => "months=0;days=0;microseconds=0",
        TableColumnValueKind.LogSequenceNumber => "0/0（WAL LSN 十六進位）",
        TableColumnValueKind.FullTextVector => "'lexeme':1A,2B（tsvector）",
        TableColumnValueKind.FullTextQuery => "'lexeme':A & !'other':*（tsquery）",
        TableColumnValueKind.PostgreSqlRange when column.DataTypeName.EndsWith(
            "multirange",
            StringComparison.OrdinalIgnoreCase) => "{[1,10),[20,30)}（最多 1 MiB 字元）",
        TableColumnValueKind.PostgreSqlRange => "[1,10) 或 empty（最多 1 MiB 字元）",
        TableColumnValueKind.PostgreSqlArray => "{1,2,3} 或 {{1,2},{3,4}}（最多 1 MiB 字元）",
        TableColumnValueKind.PostgreSqlGeometric => BuildGeometricWatermark(column),
        TableColumnValueKind.PostgreSqlServerValidatedText => BuildPostgreSqlServerTextWatermark(column),
        TableColumnValueKind.Spatial => "SRID=4326;POINT (121.5 25.0)（WKT，最多 1 MiB）",
        TableColumnValueKind.Guid => "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx",
        TableColumnValueKind.Json => "有效 JSON（最多 1 MiB 字元）",
        TableColumnValueKind.Xml => "有效 XML（最多 1 MiB 字元，禁止 DTD）",
        TableColumnValueKind.NetworkAddress => BuildNetworkWatermark(column),
        TableColumnValueKind.BitString => column.DataTypeName.StartsWith("bit varying", StringComparison.OrdinalIgnoreCase)
            ? "0/1 bit string（不可超過欄位長度）"
            : "0/1 bit string（必須符合欄位長度）",
        TableColumnValueKind.Binary => "0x00FF（二進位十六進位，最多 1 MiB）",
        _ => string.Empty
    };

    private static string BuildNetworkWatermark(TableColumnInfo column) => column.DataTypeName.ToLowerInvariant() switch
    {
        "inet" => "192.0.2.10/24 或 2001:db8::10/64",
        "cidr" => "192.0.2.0/24 或 2001:db8::/32",
        "macaddr" => "08:00:2b:01:02:03",
        "macaddr8" => "08:00:2b:ff:fe:01:02:03",
        _ => string.Empty
    };

    private static string BuildGeometricWatermark(TableColumnInfo column) =>
        column.DataTypeName.ToLowerInvariant() switch
        {
            "point" => "(1.5,2.5)",
            "line" => "{1,2,-3}",
            "lseg" => "[(1,2),(3,4)]",
            "box" => "(3,4),(1,2)",
            "path" => "[(1,2),(3,4)] 或 ((1,2),(3,4))",
            "polygon" => "((1,2),(3,4),(5,6))",
            "circle" => "<(1,2),3.5>",
            _ => string.Empty
        };

    private static string BuildPostgreSqlServerTextWatermark(TableColumnInfo column) =>
        column.DataTypeName.ToLowerInvariant() switch
        {
            "jsonpath" => "$.store.book[*] ? (@.price < 10)",
            "pg_snapshot" or "txid_snapshot" => "xmin:xmax:xip_list",
            "hstore" => "\"key\"=>\"value\"",
            "ltree" => "Top.Science.Astronomy",
            "lquery" => "Top.*{1,2}.Astronomy",
            "ltxtquery" => "Science & Astronomy",
            _ when column.DataTypeName.StartsWith("reg", StringComparison.OrdinalIgnoreCase) =>
                "物件名稱或 OID（由 PostgreSQL 驗證）",
            _ => "由 PostgreSQL 驗證格式（最多 1 MiB 字元）"
        };

    private static string BuildExactDecimalWatermark(TableColumnInfo column)
    {
        var definition = TableCellValueConverter.GetExactDecimalDefinition(column);
        var signHint = definition.IsUnsigned ? "非負；" : string.Empty;
        if (definition is not { Precision: { } precision, Scale: { } scale })
        {
            return $"{signHint}十進位數字（不使用指數或千分位）";
        }

        if (scale < 0)
        {
            return $"{signHint}最多 {precision - scale} 位整數，尾端至少 {-scale} 個 0（不可含小數）";
        }

        var leadingZeroHint = scale > precision
            ? $"，小數前 {scale - precision} 位須為 0"
            : string.Empty;
        return $"{signHint}最多 {Math.Max(0, precision - scale)} 位整數、{scale} 位小數{leadingZeroHint}（不使用指數）";
    }

    private static bool IsStructuredText(TableColumnInfo column) =>
        column.ValueKind is TableColumnValueKind.Json or TableColumnValueKind.Xml;

    private sealed record FieldEditor(
        TableColumnInfo Column,
        TextBox ValueBox,
        CheckBox NullCheck,
        CheckBox DefaultCheck,
        object? Original,
        bool ReadOnly);
}
