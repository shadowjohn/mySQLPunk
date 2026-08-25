using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using mySQLPunk.lib;

namespace mySQLPunk
{
    public sealed class QueryPlanView : UserControl
    {
        private readonly Label summaryLabel;
        private readonly TabControl views;
        private readonly TabPage visualPage;
        private readonly TabPage jsonPage;
        private readonly TabPage textPage;
        private readonly TreeView planTree;
        private readonly DataGridView detailsGrid;
        private readonly RichTextBox jsonText;
        private readonly RichTextBox planText;
        private QueryPlanDocument document;

        public QueryPlanView()
        {
            Dock = DockStyle.Fill;

            summaryLabel = new Label
            {
                Dock = DockStyle.Top,
                Height = 42,
                Padding = new Padding(UiMetrics.Space3, 0, UiMetrics.Space3, 0),
                TextAlign = ContentAlignment.MiddleLeft,
                Font = UiKit.BodyBold
            };

            views = new TabControl { Dock = DockStyle.Fill };
            visualPage = new TabPage();
            jsonPage = new TabPage();
            textPage = new TabPage();

            SplitContainer visualSplit = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                Size = new Size(900, 500),
                Panel1MinSize = 260,
                Panel2MinSize = 220,
                SplitterDistance = 520
            };

            planTree = new TreeView
            {
                Dock = DockStyle.Fill,
                HideSelection = false,
                ShowNodeToolTips = true,
                Font = UiKit.Body
            };
            planTree.AfterSelect += (s, e) => ShowNodeDetails(e.Node == null ? null : e.Node.Tag as QueryPlanNode);

            detailsGrid = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false
            };
            detailsGrid.Columns.Add("Property", string.Empty);
            detailsGrid.Columns.Add("Value", string.Empty);
            detailsGrid.Columns[0].FillWeight = 42;
            detailsGrid.Columns[1].FillWeight = 58;

            visualSplit.Panel1.Controls.Add(planTree);
            visualSplit.Panel2.Controls.Add(detailsGrid);
            visualPage.Controls.Add(visualSplit);

            jsonText = CreateReadOnlyTextBox();
            planText = CreateReadOnlyTextBox();
            jsonPage.Controls.Add(jsonText);
            textPage.Controls.Add(planText);
            views.TabPages.Add(visualPage);
            views.TabPages.Add(jsonPage);
            views.TabPages.Add(textPage);

            Controls.Add(views);
            Controls.Add(summaryLabel);
            ApplyLanguage();
            ApplyTheme();
        }

        public void LoadDocument(QueryPlanDocument value)
        {
            document = value;
            planTree.BeginUpdate();
            try
            {
                planTree.Nodes.Clear();
                if (document != null)
                {
                    foreach (QueryPlanNode root in document.Roots)
                    {
                        planTree.Nodes.Add(CreateTreeNode(root));
                    }
                    if (document.NodeCount <= 100) planTree.ExpandAll();
                    else
                    {
                        foreach (TreeNode root in planTree.Nodes) root.Expand();
                    }
                }
            }
            finally
            {
                planTree.EndUpdate();
            }

            jsonText.Text = document == null ? string.Empty : document.RawJson ?? string.Empty;
            planText.Text = document == null ? string.Empty : document.TextPlan ?? string.Empty;
            UpdateSummary();
            if (planTree.Nodes.Count > 0) planTree.SelectedNode = planTree.Nodes[0];
            else ShowNodeDetails(null);
            ApplyTheme();
        }

        public void ApplyLanguage()
        {
            visualPage.Text = Localization.T("Query.PlanVisual");
            jsonPage.Text = Localization.T("Query.PlanJson");
            textPage.Text = Localization.T("Query.PlanText");
            detailsGrid.Columns[0].HeaderText = Localization.T("Query.PlanProperty");
            detailsGrid.Columns[1].HeaderText = Localization.T("Query.PlanValue");
            UpdateSummary();
            RefreshTreeLanguage(planTree.Nodes);
            if (planTree.SelectedNode != null) ShowNodeDetails(planTree.SelectedNode.Tag as QueryPlanNode);
        }

        public void ApplyTheme()
        {
            ThemeManager.ApplyTo(this);
            BackColor = ThemeManager.WindowBackColor;
            summaryLabel.BackColor = ThemeManager.SurfaceColor;
            summaryLabel.ForeColor = ThemeManager.TextColor;
            planTree.BackColor = ThemeManager.WindowBackColor;
            planTree.ForeColor = ThemeManager.TextColor;
            jsonText.BackColor = ThemeManager.TextBoxBackColor;
            jsonText.ForeColor = ThemeManager.TextColor;
            planText.BackColor = ThemeManager.TextBoxBackColor;
            planText.ForeColor = ThemeManager.TextColor;
            detailsGrid.BackgroundColor = ThemeManager.WindowBackColor;
            detailsGrid.GridColor = ThemeManager.GridColor;
            detailsGrid.EnableHeadersVisualStyles = false;
            detailsGrid.ColumnHeadersDefaultCellStyle.BackColor = ThemeManager.SurfaceColor;
            detailsGrid.ColumnHeadersDefaultCellStyle.ForeColor = ThemeManager.TextColor;
            detailsGrid.DefaultCellStyle.BackColor = ThemeManager.ElevatedColor;
            detailsGrid.DefaultCellStyle.ForeColor = ThemeManager.TextColor;
            detailsGrid.DefaultCellStyle.SelectionBackColor = ThemeManager.SelectionColor;
            detailsGrid.DefaultCellStyle.SelectionForeColor = ThemeManager.SelectionTextColor;
            foreach (TabPage page in views.TabPages) page.BackColor = ThemeManager.WindowBackColor;
            ApplyTreeSeverityColors(planTree.Nodes);
        }

        private static RichTextBox CreateReadOnlyTextBox()
        {
            return new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                WordWrap = false,
                BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 10f),
                ScrollBars = RichTextBoxScrollBars.Both
            };
        }

        private TreeNode CreateTreeNode(QueryPlanNode node)
        {
            TreeNode treeNode = new TreeNode(BuildTreeLabel(node))
            {
                Tag = node,
                ToolTipText = BuildToolTip(node)
            };
            ApplyTreeSeverityColor(treeNode, node.Severity);
            foreach (QueryPlanNode child in node.Children)
            {
                treeNode.Nodes.Add(CreateTreeNode(child));
            }
            return treeNode;
        }

        private string BuildTreeLabel(QueryPlanNode node)
        {
            List<string> parts = new List<string>();
            string severity = SeverityText(node.Severity);
            if (severity.Length > 0) parts.Add("[" + severity + "]");
            parts.Add(string.IsNullOrWhiteSpace(node.NodeType) ? Localization.T("Query.PlanNode") : node.NodeType);
            if (!string.IsNullOrWhiteSpace(node.RelationName)) parts.Add(node.RelationName);
            if (!string.IsNullOrWhiteSpace(node.AccessType)) parts.Add(node.AccessType);

            string label = string.Join(" · ", parts.ToArray());
            List<string> metrics = new List<string>();
            if (node.TotalCost.HasValue) metrics.Add(Localization.Format("Query.PlanCostMetric", FormatNumber(node.TotalCost.Value)));
            if (node.EstimatedRows.HasValue) metrics.Add(Localization.Format("Query.PlanRowsMetric", FormatNumber(node.EstimatedRows.Value)));
            if (node.ActualTotalTimeMs.HasValue) metrics.Add(Localization.Format("Query.PlanTimeMetric", FormatNumber(node.ActualTotalTimeMs.Value)));
            if (metrics.Count > 0) label += "  |  " + string.Join("  |  ", metrics.ToArray());
            return label;
        }

        private string BuildToolTip(QueryPlanNode node)
        {
            List<string> lines = new List<string> { BuildTreeLabel(node) };
            if (!string.IsNullOrWhiteSpace(node.JoinType)) lines.Add(Localization.T("Query.PlanJoinType") + ": " + node.JoinType);
            return string.Join(Environment.NewLine, lines.ToArray());
        }

        private void ShowNodeDetails(QueryPlanNode node)
        {
            detailsGrid.Rows.Clear();
            if (node == null) return;

            AddDetail("Query.PlanNodeType", node.NodeType);
            AddDetail("Query.PlanRelation", node.RelationName);
            AddDetail("Query.PlanAlias", node.Alias);
            AddDetail("Query.PlanAccessType", node.AccessType);
            AddDetail("Query.PlanJoinType", node.JoinType);
            AddDetail("Query.PlanStartupCost", FormatNullable(node.StartupCost));
            AddDetail("Query.PlanTotalCost", FormatNullable(node.TotalCost));
            AddDetail("Query.PlanEstimatedRows", FormatNullable(node.EstimatedRows));
            AddDetail("Query.PlanActualRows", FormatNullable(node.ActualRows));
            AddDetail("Query.PlanActualTime", node.ActualTotalTimeMs.HasValue ? FormatNumber(node.ActualTotalTimeMs.Value) + " ms" : string.Empty);
            AddDetail("Query.PlanSeverity", SeverityText(node.Severity, true));
            foreach (KeyValuePair<string, string> detail in node.Details.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(detail.Value)) continue;
                detailsGrid.Rows.Add(detail.Key, detail.Value);
            }
            detailsGrid.ClearSelection();
        }

        private void AddDetail(string localizationKey, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            detailsGrid.Rows.Add(Localization.T(localizationKey), value);
        }

        private void UpdateSummary()
        {
            if (document == null)
            {
                summaryLabel.Text = Localization.T("Query.PlanSummaryEmpty");
                return;
            }

            List<string> metrics = new List<string>
            {
                Localization.Format("Query.PlanProviderSummary", ProviderDisplayName(document.Provider)),
                Localization.Format("Query.PlanNodeCountSummary", document.NodeCount)
            };
            if (document.TotalCost.HasValue) metrics.Add(Localization.Format("Query.PlanTotalCostSummary", FormatNumber(document.TotalCost.Value)));
            if (document.PlanningTimeMs.HasValue) metrics.Add(Localization.Format("Query.PlanPlanningTimeSummary", FormatNumber(document.PlanningTimeMs.Value)));
            if (document.ExecutionTimeMs.HasValue) metrics.Add(Localization.Format("Query.PlanExecutionTimeSummary", FormatNumber(document.ExecutionTimeMs.Value)));
            summaryLabel.Text = string.Join("  |  ", metrics.ToArray());
        }

        private string SeverityText(QueryPlanSeverity severity, bool includeNormal = false)
        {
            if (severity == QueryPlanSeverity.High) return Localization.T("Query.PlanHighCost");
            if (severity == QueryPlanSeverity.Medium) return Localization.T("Query.PlanMediumCost");
            return includeNormal ? Localization.T("Query.PlanNormalCost") : string.Empty;
        }

        private void ApplyTreeSeverityColors(TreeNodeCollection nodes)
        {
            foreach (TreeNode treeNode in nodes)
            {
                QueryPlanNode node = treeNode.Tag as QueryPlanNode;
                ApplyTreeSeverityColor(treeNode, node == null ? QueryPlanSeverity.Normal : node.Severity);
                ApplyTreeSeverityColors(treeNode.Nodes);
            }
        }

        private void RefreshTreeLanguage(TreeNodeCollection nodes)
        {
            foreach (TreeNode treeNode in nodes)
            {
                QueryPlanNode node = treeNode.Tag as QueryPlanNode;
                if (node != null)
                {
                    treeNode.Text = BuildTreeLabel(node);
                    treeNode.ToolTipText = BuildToolTip(node);
                }
                RefreshTreeLanguage(treeNode.Nodes);
            }
        }

        private static void ApplyTreeSeverityColor(TreeNode treeNode, QueryPlanSeverity severity)
        {
            treeNode.ForeColor = severity == QueryPlanSeverity.High
                ? ThemeManager.DangerColor
                : severity == QueryPlanSeverity.Medium
                    ? ThemeManager.WarningColor
                    : ThemeManager.TextColor;
        }

        private static string ProviderDisplayName(string provider)
        {
            if (string.Equals(provider, "mysql", StringComparison.OrdinalIgnoreCase)) return "MySQL / MariaDB";
            if (string.Equals(provider, "postgresql", StringComparison.OrdinalIgnoreCase)) return "PostgreSQL";
            return provider ?? string.Empty;
        }

        private static string FormatNullable(double? value)
        {
            return value.HasValue ? FormatNumber(value.Value) : string.Empty;
        }

        private static string FormatNumber(double value)
        {
            return value.ToString(value == Math.Truncate(value) ? "0" : "0.###", CultureInfo.InvariantCulture);
        }
    }
}
