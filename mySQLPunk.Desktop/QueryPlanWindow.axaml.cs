using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using MySqlPunk.Core.Models;

namespace MySqlPunk.Desktop;

public sealed partial class QueryPlanWindow : Window
{
    private static readonly IBrush NormalBrush = new SolidColorBrush(Color.Parse("#98A2B3"));
    private static readonly IBrush MediumBrush = new SolidColorBrush(Color.Parse("#F79009"));
    private static readonly IBrush HighBrush = new SolidColorBrush(Color.Parse("#D92D20"));

    private readonly TreeView _planTree;
    private readonly DataGrid _detailGrid;
    private readonly TextBlock _detailTitleText;
    private readonly TextBox _textPlanBox;
    private readonly string _textPlan;

    public QueryPlanWindow()
        : this(new QueryPlanDocument(), string.Empty)
    {
    }

    public QueryPlanWindow(QueryPlanDocument document, string connectionName)
    {
        AvaloniaXamlLoader.Load(this);
        _planTree = this.FindControl<TreeView>("PlanTree")!;
        _detailGrid = this.FindControl<DataGrid>("DetailGrid")!;
        _detailTitleText = this.FindControl<TextBlock>("DetailTitleText")!;
        _textPlanBox = this.FindControl<TextBox>("TextPlanBox")!;
        _textPlan = document.TextPlan;

        this.FindControl<TextBlock>("TitleText")!.Text = string.IsNullOrWhiteSpace(connectionName)
            ? "執行計畫"
            : $"執行計畫 · {connectionName}";
        this.FindControl<TextBlock>("SummaryText")!.Text = document.Summary;
        this.FindControl<TextBlock>("ExplainSqlText")!.Text = document.ExplainSql;
        this.FindControl<TabItem>("RawTab")!.Header = string.IsNullOrWhiteSpace(document.RawFormat)
            ? "原始資料"
            : $"原始資料（{document.RawFormat}）";
        _textPlanBox.Text = document.TextPlan;
        this.FindControl<TextBox>("RawPlanBox")!.Text = document.RawPlan;

        var roots = document.Roots.Select(root => new PlanTreeItem(root)).ToList();
        _planTree.ItemsSource = roots;
        if (roots.Count > 0)
        {
            _planTree.SelectedItem = roots[0];
            ShowDetails(roots[0]);
        }
    }

    private void PlanTree_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_planTree.SelectedItem is PlanTreeItem item)
        {
            ShowDetails(item);
        }
    }

    private void ShowDetails(PlanTreeItem item)
    {
        var node = item.Node;
        var rows = new List<PlanDetail>();
        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                rows.Add(new PlanDetail(key, value));
            }
        }

        Add("節點類型", node.NodeType);
        Add("物件", node.RelationName);
        Add("別名", node.Alias);
        Add("存取方式", node.AccessType);
        Add("Join 類型", node.JoinType);
        Add("起始成本", node.StartupCost is { } startup ? QueryPlanFormatting.Number(startup) : null);
        Add("總成本", node.TotalCost is { } total ? QueryPlanFormatting.Number(total) : null);
        Add("估計列數", node.EstimatedRows is { } estimated ? QueryPlanFormatting.Number(estimated) : null);
        Add("實際列數", node.ActualRows is { } actual ? QueryPlanFormatting.Number(actual) : null);
        Add("實際時間 (ms)", node.ActualTotalTimeMs is { } time ? QueryPlanFormatting.Number(time) : null);
        Add("相對成本", node.Severity switch
        {
            QueryPlanSeverity.High => "高（≥ 50%）",
            QueryPlanSeverity.Medium => "中（≥ 20%）",
            _ => null
        });
        foreach (var detail in node.Details.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            rows.Add(new PlanDetail(detail.Key, detail.Value));
        }

        _detailTitleText.Text = $"節點屬性 · {node.NodeType}";
        _detailGrid.ItemsSource = rows;
    }

    private async void CopyText_Click(object? sender, RoutedEventArgs e)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(_textPlan);
            await clipboard.FlushAsync();
        }
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private sealed record PlanDetail(string Key, string Value);

    private sealed class PlanTreeItem
    {
        public PlanTreeItem(QueryPlanNode node)
        {
            Node = node;
            Children = node.Children.Select(child => new PlanTreeItem(child)).ToList();
        }

        public QueryPlanNode Node { get; }

        public string Text => Node.Summary;

        public IBrush SeverityBrush => Node.Severity switch
        {
            QueryPlanSeverity.High => HighBrush,
            QueryPlanSeverity.Medium => MediumBrush,
            _ => NormalBrush
        };

        public IReadOnlyList<PlanTreeItem> Children { get; }
    }
}
