using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MySqlPunk.Core.Services;

namespace MySqlPunk.Desktop;

/// <summary>
/// 資料產生器字典管理：內建字典唯讀，自訂字典可新增、編輯與刪除；檔案格式與 Windows 版相同。
/// </summary>
internal sealed class DataGeneratorDictionaryWindow : Window
{
    private readonly string _directory;
    private readonly ListBox _list = new();
    private readonly TextBox _name = new() { PlaceholderText = "字典名稱" };
    private readonly TextBox _content = new()
    {
        AcceptsReturn = true,
        AcceptsTab = true,
        TextWrapping = TextWrapping.NoWrap,
        FontFamily = new FontFamily("monospace")
    };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly Button _save = new() { Content = "儲存" };
    private readonly Button _delete = new() { Content = "刪除", Margin = new Thickness(8, 0, 0, 0) };
    private string? _loaded;

    public DataGeneratorDictionaryWindow(string directory)
    {
        _directory = directory;
        Title = "資料產生器字典";
        Width = 860;
        Height = 580;
        MinWidth = 620;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var create = new Button { Content = "新增", Margin = new Thickness(0, 8, 0, 0) };
        create.Click += (_, _) =>
        {
            _list.SelectedItem = null;
            _loaded = null;
            _name.Text = string.Empty;
            _name.IsReadOnly = false;
            _content.Text = string.Empty;
            _content.IsReadOnly = false;
            _status.Text = "輸入名稱與內容後按「儲存」。";
            UpdateButtons();
        };
        var left = new DockPanel { Width = 230, Margin = new Thickness(0, 0, 12, 0) };
        DockPanel.SetDock(create, Dock.Bottom);
        left.Children.Add(create);
        left.Children.Add(_list);

        var help = new TextBlock
        {
            Text = "一行一個值；要加權重時在值後面加 Tab 與 1–1,000,000 的整數（例如 paid[Tab]50）。# 開頭為註解。",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.Parse("#475467")),
            Margin = new Thickness(0, 6, 0, 6)
        };
        var editor = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        editor.Children.Add(_name);
        Grid.SetRow(help, 1);
        editor.Children.Add(help);
        Grid.SetRow(_content, 2);
        editor.Children.Add(_content);
        Grid.SetRow(_status, 3);
        _status.Margin = new Thickness(0, 6, 0, 0);
        editor.Children.Add(_status);

        var close = new Button { Content = "關閉", Margin = new Thickness(8, 0, 0, 0) };
        close.Click += (_, _) => Close();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        actions.Children.Add(_save);
        actions.Children.Add(_delete);
        actions.Children.Add(close);

        var body = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(actions, Dock.Bottom);
        body.Children.Add(actions);
        DockPanel.SetDock(left, Dock.Left);
        body.Children.Add(left);
        body.Children.Add(editor);
        Content = body;

        _list.SelectionChanged += (_, _) =>
        {
            if (_list.SelectedItem is string name)
            {
                Show(name);
            }
        };
        _save.Click += (_, _) => Guard(Save);
        _delete.Click += (_, _) => Guard(Delete);
        Reload(null);
    }

    private void Reload(string? select)
    {
        var names = DataGeneratorDictionaryStore.List(_directory).ToList();
        _list.ItemsSource = names;
        _list.SelectedItem = select is not null && names.Contains(select) ? select : names.FirstOrDefault();
    }

    private void Show(string name)
    {
        var builtIn = DataGeneratorDictionaryStore.IsBuiltIn(name);
        _loaded = name;
        _name.Text = name;
        _name.IsReadOnly = builtIn;
        _content.IsReadOnly = builtIn;
        try
        {
            var dictionary = DataGeneratorDictionaryStore.Load(_directory, name);
            _content.Text = dictionary is null ? string.Empty : DataGeneratorDictionaryStore.Serialize(dictionary);
            _status.Foreground = Brushes.Black;
            _status.Text = dictionary is null
                ? string.Empty
                : $"{dictionary.Values.Count:N0} 個值，權重合計 {dictionary.TotalWeight:N0}。" + (builtIn ? " 內建字典為唯讀，可另存成自訂字典後修改。" : string.Empty);
        }
        catch (InvalidOperationException exception)
        {
            _content.Text = string.Empty;
            SetError(exception.Message);
        }

        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var builtIn = DataGeneratorDictionaryStore.IsBuiltIn(_loaded);
        _delete.IsEnabled = _loaded is not null && !builtIn;
        _save.Content = builtIn ? "另存成自訂字典" : "儲存";
    }

    private void Save()
    {
        var target = (_name.Text ?? string.Empty).Trim();
        if (DataGeneratorDictionaryStore.IsBuiltIn(target))
        {
            var existing = DataGeneratorDictionaryStore.List(_directory);
            var candidate = target + " copy";
            var suffix = 2;
            while (existing.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                candidate = $"{target} copy {suffix++}";
            }

            target = candidate;
        }

        var saved = DataGeneratorDictionaryStore.Save(_directory, target, _content.Text);
        if (_loaded is not null && !DataGeneratorDictionaryStore.IsBuiltIn(_loaded) && !string.Equals(_loaded, target, StringComparison.OrdinalIgnoreCase))
        {
            DataGeneratorDictionaryStore.Delete(_directory, _loaded);
        }

        Reload(target);
        _status.Foreground = Brushes.Black;
        _status.Text = $"已儲存「{target}」：{saved.Values.Count:N0} 個值。";
    }

    private void Delete()
    {
        if (_loaded is null || DataGeneratorDictionaryStore.IsBuiltIn(_loaded))
        {
            return;
        }

        DataGeneratorDictionaryStore.Delete(_directory, _loaded);
        Reload(null);
    }

    private void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            SetError(exception.Message);
        }
    }

    private void SetError(string message)
    {
        _status.Foreground = new SolidColorBrush(Color.Parse("#B42318"));
        _status.Text = message;
    }
}
