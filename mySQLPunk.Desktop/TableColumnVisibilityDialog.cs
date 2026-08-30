using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MySqlPunk.Core.Models;

namespace MySqlPunk.Desktop;

internal sealed class TableColumnVisibilityDialog : Window
{
    private readonly List<ColumnChoice> _choices = new();
    private readonly TextBlock _errorText;

    public TableColumnVisibilityDialog(
        IReadOnlyList<TableColumnInfo> columns,
        IReadOnlySet<string> visibleColumnNames)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(visibleColumnNames);

        Title = "選擇顯示欄位";
        Width = 560;
        Height = 620;
        MinWidth = 430;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var fields = new StackPanel { Spacing = 8 };
        foreach (var column in columns.OrderBy(column => column.Ordinal))
        {
            var checkBox = new CheckBox
            {
                Content = BuildColumnLabel(column),
                IsChecked = visibleColumnNames.Contains(column.Name)
            };
            _choices.Add(new ColumnChoice(column.Name, checkBox));
            fields.Children.Add(checkBox);
        }

        var showAll = new Button
        {
            Content = "全部顯示",
            Padding = new Thickness(12, 6)
        };
        showAll.Click += (_, _) => SetAll(isVisible: true);
        var hideAll = new Button
        {
            Content = "全部隱藏",
            Padding = new Thickness(12, 6)
        };
        hideAll.Click += (_, _) => SetAll(isVisible: false);
        var presets = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { showAll, hideAll }
        };

        _errorText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.Parse("#B42318")),
            TextWrapping = TextWrapping.Wrap
        };

        var cancel = new Button
        {
            Content = "取消",
            Padding = new Thickness(14, 7),
            MinWidth = 80
        };
        cancel.Click += (_, _) => Close(null);
        var apply = new Button
        {
            Content = "套用",
            Padding = new Thickness(14, 7),
            MinWidth = 80
        };
        apply.Click += (_, _) => Apply();
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancel, apply }
        };

        var layout = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto"),
            RowSpacing = 12
        };
        layout.Children.Add(new TextBlock
        {
            Text = "取消勾選可暫時隱藏欄位；資料仍保留供安全修改與衝突比對，本頁匯出只包含畫面可見欄位。",
            Foreground = new SolidColorBrush(Color.Parse("#475467")),
            TextWrapping = TextWrapping.Wrap
        });
        Grid.SetRow(presets, 1);
        layout.Children.Add(presets);
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = fields
        };
        Grid.SetRow(scroll, 2);
        layout.Children.Add(scroll);
        Grid.SetRow(_errorText, 3);
        layout.Children.Add(_errorText);
        Grid.SetRow(actions, 4);
        layout.Children.Add(actions);

        Content = new Border
        {
            Padding = new Thickness(20),
            Child = layout
        };
    }

    private void SetAll(bool isVisible)
    {
        foreach (var choice in _choices)
        {
            choice.CheckBox.IsChecked = isVisible;
        }

        _errorText.Text = string.Empty;
    }

    private void Apply()
    {
        var selected = _choices
            .Where(choice => choice.CheckBox.IsChecked == true)
            .Select(choice => choice.ColumnName)
            .ToHashSet(StringComparer.Ordinal);
        if (selected.Count == 0)
        {
            _errorText.Text = "至少保留一個顯示欄位。";
            return;
        }

        Close(selected);
    }

    private static string BuildColumnLabel(TableColumnInfo column)
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

        var suffix = attributes.Count == 0 ? string.Empty : $" · {string.Join(" · ", attributes)}";
        return $"{column.Name} · {column.DataTypeName}{suffix}";
    }

    private sealed record ColumnChoice(string ColumnName, CheckBox CheckBox);
}
