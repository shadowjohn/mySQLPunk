using System.Text;
using Avalonia.Controls;
using Avalonia.Layout;
using MySqlPunk.Core.Models;
using MySqlPunk.Core.Services;

namespace MySqlPunk.Desktop;

public sealed class LogicalMigrationWindow : Window
{
    private readonly ConnectionProfile _source;
    private readonly ComboBox _target = new() { Name = "MigrationTarget", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _version = new() { ItemsSource = new[] { "SQLite 3（3.37–3.53）", "PostgreSQL 14", "PostgreSQL 15", "PostgreSQL 16", "PostgreSQL 17", "MySQL 8.4", "MariaDB 11.4" } };
    private readonly TextBox _database = new() { Name = "MigrationDatabase", PlaceholderText = "另建空目標資料庫名稱／SQLite 完整檔案路徑" };
    private readonly TextBox _password = new() { PasswordChar = '●', PlaceholderText = "目標密碼（只留記憶體，不讀秘密庫）" };
    private readonly TextBox _sshPassword = new() { PasswordChar = '●', PlaceholderText = "目標 SSH 密碼（如需要）" };
    private readonly TextBox _passphrase = new() { PasswordChar = '●', PlaceholderText = "目標 SSH key passphrase（如需要）" };
    private readonly TextBox _confirmation = new() { Name = "MigrationConfirmation", PlaceholderText = "確認後完整輸入目標名稱／路徑" };
    private readonly TextBox _report = new() { Name = "MigrationReport", IsReadOnly = true, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MinHeight = 240 };
    private readonly Button _inspect = new() { Name = "MigrationInspect", Content = "唯讀預檢與 SQL 預覽" };
    private readonly Button _apply = new() { Name = "MigrationApply", Content = "確認並搬移至空目標", IsEnabled = false };
    private readonly Button _cancel = new() { Name = "MigrationCancel", Content = "取消", IsEnabled = false };
    private CancellationTokenSource? _running;
    private readonly CancellationToken _parentToken;
    private LogicalMigrationPreview? _preview;
    private ConnectionProfile? _approvedTarget;
    private int _approvedMajor;
    private bool _retryBlocked;

    public LogicalMigrationWindow(ConnectionProfile source, IReadOnlyList<ConnectionProfile> targets, CancellationToken parentToken)
    {
        _source = source.Clone(); _parentToken = parentToken;
        Title = "可驗證邏輯搬移（受控關聯式子集）"; Width = 850; Height = 790; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var profiles = targets.Select(profile => profile.Clone()).ToArray();
        _target.ItemsSource = profiles.Select(profile => profile.Name + " · " + profile.ProviderDisplayName).ToArray();
        var fields = new StackPanel { Spacing = 8, Margin = new Avalonia.Thickness(16) };
        fields.Children.Add(new TextBlock { Text = "來源：" + source.ProviderDisplayName + " / " + source.Database, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        fields.Children.Add(new TextBlock { Text = "不是原生備份 restore。SQLite STRICT ↔ PostgreSQL 14–17：INTEGER/bigint、BLOB/bytea、有效 UTF-8 TEXT（BINARY ↔ C）、NULL/NOT NULL、非自增單欄／複合主鍵、普通升冪非 unique 索引。SQLite 主鍵表須 WITHOUT ROWID 且欄位明確 NOT NULL；nullable 索引採 NULLS FIRST。FK、其他定序、default、identity/sequence、expression/partial/unique 索引、trigger/procedure 仍預檢阻擋。目標須事先另建且為空。最多 100,000 列／64 MiB、單欄 1 MiB，單一交易＋結構與內容校驗，不逐表續寫。", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        foreach (var control in new Control[] { _target, _database, _version, _password, _sshPassword, _passphrase, _confirmation }) fields.Children.Add(control);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(_inspect); buttons.Children.Add(_apply); buttons.Children.Add(_cancel); fields.Children.Add(buttons);
        fields.Children.Add(_report); Content = new ScrollViewer { Content = fields };
        fields.Children.Insert(2, new TextBlock { Text = "MySQL 8.4／MariaDB 11.4 ↔ PostgreSQL：signed BIGINT、LONGTEXT utf8mb4 NO PAD binary ↔ text C、LONGBLOB ↔ bytea，主鍵／普通索引只允許整數欄。MySQL 目標 DDL 隱含提交：先建立本輪專屬 staging、交易分批寫入／校驗，再單次 RENAME 發布／再次校驗；取消或失敗可能殘留 staging，列明物件並禁止盲重試，不自動刪除。發布送出後取消須等實際結果，不保證撤銷。MySQL 只驗證 InnoDB Dynamic、lower_case_table_names=0，要求目前帳號有可證明的完整 catalog 可見權限，不新增權限。此路徑不是下述 SQLite／PG 的單一 DDL 交易。", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        _target.SelectionChanged += (_, _) =>
        {
            _preview = null; _apply.IsEnabled = false;
            if (_target.SelectedIndex >= 0) _database.Text = profiles[_target.SelectedIndex].Database;
        };
        _version.SelectionChanged += (_, _) => InvalidatePreview();
        foreach (var input in new[] { _database, _password, _sshPassword, _passphrase })
            input.PropertyChanged += (_, args) => { if (args.Property == TextBox.TextProperty) InvalidatePreview(); };
        _inspect.Click += async (_, _) => await WorkAsync(async token =>
        {
            if (_target.SelectedIndex < 0 || _version.SelectedIndex < 0) throw new InvalidOperationException("請明確選取目標連線與版本。");
            var target = profiles[_target.SelectedIndex].Clone(); target.Database = _database.Text ?? "";
            target.Password = _password.Text ?? ""; target.SshPassword = _sshPassword.Text ?? ""; target.SshKeyPassphrase = _passphrase.Text ?? "";
            int major = SelectedMajor();
            var preview = await LogicalMigrationService.PreviewAsync(_source, target, major, token);
            token.ThrowIfCancellationRequested();
            _preview = preview; _approvedTarget = target; _approvedMajor = major;
            var text = new StringBuilder("來源 " + preview.SourceVersion + " → 目標 " + preview.TargetVersion + "\n目標：" + preview.TargetDatabase + "\n");
            foreach (var table in preview.Tables) text.AppendLine(table.Name + "：" + table.Rows + " 列；整數／文字／二進位、主鍵與 " + table.Indexes.Count + " 個索引校驗（預覽不顯示資料值）");
            foreach (var sql in preview.Statements) text.AppendLine(sql);
            foreach (var blocker in preview.Blockers) text.AppendLine("阻擋：" + blocker);
            _report.Text = text.ToString();
        });
        _apply.Click += async (_, _) => await WorkAsync(async token =>
        {
            if (_preview?.CanStart != true || _approvedTarget == null || _confirmation.Text != _approvedTarget.Database ||
                _database.Text != _approvedTarget.Database || _target.SelectedIndex < 0 ||
                profiles[_target.SelectedIndex].Id != _approvedTarget.Id ||
                SelectedMajor() != _approvedMajor ||
                (_password.Text ?? "") != _approvedTarget.Password || (_sshPassword.Text ?? "") != _approvedTarget.SshPassword ||
                (_passphrase.Text ?? "") != _approvedTarget.SshKeyPassphrase)
                throw new InvalidOperationException("請完成預檢並輸入完整目標名稱；沒有寫入。");
            var result = await LogicalMigrationService.RunAsync(_source, _approvedTarget, _approvedMajor, _preview, _confirmation.Text!, token);
            _preview = null;
            _retryBlocked = result.RetryBlocked;
            _report.Text = result.Outcome + "：" + result.Rows + " 列\n" + result.Report;
        });
        _cancel.Click += (_, _) => _running?.Cancel();
        Closing += (_, args) => { if (_running != null) { args.Cancel = true; _running.Cancel(); } };
        Closed += (_, _) => { _password.Text = ""; _sshPassword.Text = ""; _passphrase.Text = ""; _approvedTarget = null; };
    }

    private async Task WorkAsync(Func<CancellationToken, Task> operation)
    {
        if (_running != null || _retryBlocked || _parentToken.IsCancellationRequested) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_parentToken);
        _running = cancellation;
        SetBusy(true);
        try { await operation(cancellation.Token); }
        catch (OperationCanceledException) { _preview = null; _report.Text = "已取消預檢／尚未寫入的操作；需重新預檢。"; }
        catch (Exception exception) { _preview = null; _report.Text = exception is InvalidOperationException ? exception.Message : "預檢／連線失敗：" + exception.GetType().Name + "；沒有自動重試。"; }
        finally { _running = null; SetBusy(false); }
    }

    private void SetBusy(bool busy)
    {
        foreach (var control in new Control[] { _target, _database, _version, _password, _sshPassword, _passphrase, _confirmation }) control.IsEnabled = !busy && !_retryBlocked;
        _inspect.IsEnabled = !busy && !_retryBlocked;
        _apply.IsEnabled = !busy && !_retryBlocked && _preview?.CanStart == true;
        _cancel.IsEnabled = busy;
    }

    private void InvalidatePreview()
    {
        _preview = null; _approvedTarget = null; _apply.IsEnabled = false;
    }

    private int SelectedMajor() => _version.SelectedIndex switch { 0 => 3, 5 => 8, 6 => 11, _ => 13 + _version.SelectedIndex };
}
