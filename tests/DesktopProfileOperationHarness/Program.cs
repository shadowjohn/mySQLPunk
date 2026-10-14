using System.Reflection;
using Microsoft.Data.Sqlite;
using MySqlPunk.Core.Services;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using MySqlPunk.Core.Models;
using MySqlPunk.Desktop;

var fixtureRoot = Environment.GetEnvironmentVariable("MYSQLPUNK_PROFILE_TEST_ROOT") ?? throw new InvalidOperationException("Use Run-DesktopProfileOperationTests.sh for isolated configuration.");
var store = new ConnectionProfileStore();
if (!Path.GetFullPath(store.FilePath).StartsWith(Path.GetFullPath(fixtureRoot) + Path.DirectorySeparatorChar, StringComparison.Ordinal) || File.Exists(store.FilePath))
    throw new InvalidOperationException("Refusing to access non-isolated or existing profile settings.");
var databasePath = Path.Combine(fixtureRoot, "fixture.db");
using (var database = new SqliteConnection("Data Source=" + databasePath))
{
    database.Open();
    using var command = database.CreateCommand();
    command.CommandText = "CREATE TABLE fixture (id INTEGER NOT NULL, label BLOB) STRICT; INSERT INTO fixture VALUES (1, X'00FF');";
    command.ExecuteNonQuery();
}
await store.SaveAsync(new[] { new ConnectionProfile { Name = "Isolated profile operations", Provider = DatabaseProviderKind.Sqlite, Database = databasePath } });

return AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().AfterSetup(builder =>
    Dispatcher.UIThread.Post(async () =>
    {
        var lifetime = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        try
        {
            var window = (MainWindow)lifetime.MainWindow!;
            var profiles = window.FindControl<ListBox>("ProfilesList")!;
            await Wait(() => profiles.SelectedItem is ConnectionProfile);
            Invoke(window, "ConnectProfile_Click", null!, null!);
            await Wait(() => Field(window, "_session") != null && Field(window, "_operationCancellation") == null);
            var session = Field(window, "_session");
            var selected = (ConnectionProfile)profiles.SelectedItem!;
            var originalName = selected.Name;
            const string calculation = "WITH RECURSIVE numbers(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM numbers WHERE n < 50000000) SELECT sum(n) FROM numbers;";
            window.FindControl<TextBox>("SqlEditor")!.Text = calculation;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var pending = (Task)Invoke(window, "ExecuteCurrentSqlAsync", false)!;
            Check(stopwatch.Elapsed < TimeSpan.FromMilliseconds(250) && !pending.IsCompleted, "Actual SQL handler returns promptly during real SQLite calculation");
            bool ticked = false;
            Dispatcher.UIThread.Post(() => ticked = true);
            await Task.Delay(50);
            Check(ticked && !pending.IsCompleted, "Single UI thread keeps ticking while SQLite is executing");
            var activeCancellation = Field(window, "_operationCancellation");
            Invoke(window, "ExecuteSql_Click", null!, null!);
            Check(ReferenceEquals(activeCancellation, Field(window, "_operationCancellation")), "Repeated actual SQL click retains original operation");
            foreach (var handler in new[] { "EditProfile_Click", "DeleteProfile_Click", "ConnectProfile_Click" })
                Invoke(window, handler, null!, null!);
            Check(!window.OwnedWindows.Any() && ReferenceEquals(session, Field(window, "_session")), "Busy actual handlers reject edit/delete/connect without dialogs or disconnect");
            Invoke(window, "CancelOperation_Click", null!, null!);
            Invoke(window, "EditProfile_Click", null!, null!);
            Check(Field(window, "_operationCancellation") != null && !window.OwnedWindows.Any(), "Cancel retains real lease until pending operation completes");
            await pending.WaitAsync(TimeSpan.FromSeconds(3));
            Check(stopwatch.Elapsed < TimeSpan.FromSeconds(3) && Field(window, "_operationCancellation") == null, "Actual Cancel stops SQLite before releasing operation gate");
            window.FindControl<TextBox>("SqlEditor")!.Text = "SELECT 42;";
            await (Task)Invoke(window, "ExecuteCurrentSqlAsync", false)!;
            Check(!window.OwnedWindows.Any() && ReferenceEquals(session, Field(window, "_session")), "Query after native interruption succeeds without disconnecting");
            foreach (var handler in new[] { "EditProfile_Click", "DeleteProfile_Click" })
            {
                Invoke(window, handler, null!, null!);
                await Wait(() => window.OwnedWindows.Any());
                var dialog = window.OwnedWindows.Single();
                int changes = 0;
                Func<CancellationToken, Task> competing = token => { changes++; return Task.CompletedTask; };
                await (Task)Invoke(window, "RunOperationAsync", "must not execute", competing)!;
                Check(changes == 0 && Field(window, "_operationCancellation") != null, "Actual " + handler + " dialog holds shared gate");
                ((CancellationTokenSource)Field(window, "_operationCancellation")!).Cancel();
                if (handler == "EditProfile_Click")
                {
                    var edited = selected.Clone(); edited.Name = "must-not-save";
                    dialog.Close(edited);
                }
                else dialog.Close(true);
                await Wait(() => Field(window, "_operationCancellation") == null);
                Check(ReferenceEquals(session, Field(window, "_session")) && ((ConnectionProfile)profiles.SelectedItem!).Name == originalName, "Actual " + handler + " post-await cancellation preserves profile and live isolated session");
            }
            var migrationTarget = Path.Combine(fixtureRoot, "logical-target.db");
            using (var database = new SqliteConnection("Data Source=" + migrationTarget)) database.Open();
            Invoke(window, "LogicalMigration_Click", null!, null!);
            await Wait(() => window.OwnedWindows.OfType<LogicalMigrationWindow>().Any());
            var migration = window.OwnedWindows.OfType<LogicalMigrationWindow>().Single();
            var migrationCancellation = Field(window, "_operationCancellation");
            Invoke(window, "ExecuteSql_Click", null!, null!);
            Invoke(window, "EditProfile_Click", null!, null!);
            Invoke(window, "DeleteProfile_Click", null!, null!);
            Check(window.OwnedWindows.Count() == 1 && ReferenceEquals(migrationCancellation, Field(window, "_operationCancellation")), "Actual migration dialog holds parent SQL/profile gate");
            ((ComboBox)Field(migration, "_target")!).SelectedIndex = 0;
            ((ComboBox)Field(migration, "_version")!).SelectedIndex = 0;
            ((TextBox)Field(migration, "_database")!).Text = migrationTarget;
            var inspectMigration = (Button)Field(migration, "_inspect")!;
            var applyMigration = (Button)Field(migration, "_apply")!;
            inspectMigration.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await Wait(() => inspectMigration.IsEnabled && applyMigration.IsEnabled);
            Check(((TextBox)Field(migration, "_report")!).Text!.Contains("STRICT"), "Actual migration UI renders schema/data preview without row values");
            ((TextBox)Field(migration, "_database")!).Text = migrationTarget + ".changed";
            Check(!applyMigration.IsEnabled, "Changed target invalidates UI approval");
            ((TextBox)Field(migration, "_database")!).Text = migrationTarget;
            inspectMigration.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await Wait(() => inspectMigration.IsEnabled && applyMigration.IsEnabled);
            ((TextBox)Field(migration, "_confirmation")!).Text = "wrong";
            applyMigration.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await Wait(() => inspectMigration.IsEnabled && !applyMigration.IsEnabled);
            Check(((TextBox)Field(migration, "_report")!).Text!.Contains("沒有寫入"), "Actual migration UI rejects wrong target confirmation");
            inspectMigration.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await Wait(() => inspectMigration.IsEnabled && applyMigration.IsEnabled);
            ((TextBox)Field(migration, "_confirmation")!).Text = migrationTarget;
            applyMigration.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await Wait(() => ((TextBox)Field(migration, "_report")!).Text!.StartsWith("Verified"));
            Check(!inspectMigration.IsEnabled && !applyMigration.IsEnabled, "Verified UI completion forbids duplicate resume");
            using (var database = new SqliteConnection("Data Source=" + migrationTarget))
            {
                database.Open(); using var command = database.CreateCommand(); command.CommandText = "SELECT count(*) FROM fixture WHERE id=1 AND label=X'00FF';";
                Check(Convert.ToInt64(command.ExecuteScalar()) == 1, "Actual UI transfer preserves integer and binary data");
            }
            migration.Close();
            await Wait(() => Field(window, "_operationCancellation") == null);
            Invoke(window, "EditProfile_Click", null!, null!);
            await Wait(() => window.OwnedWindows.Any());
            var saved = selected.Clone(); saved.Name = "Isolated edited profile";
            window.OwnedWindows.Single().Close(saved);
            await Wait(() => Field(window, "_operationCancellation") == null);
            Check(((ConnectionProfile)profiles.SelectedItem!).Name == saved.Name && Field(window, "_session") == null, "Idle edit still saves and disconnects under the lease");
            Invoke(window, "ConnectProfile_Click", null!, null!);
            await Wait(() => Field(window, "_session") != null && Field(window, "_operationCancellation") == null);
            Invoke(window, "DeleteProfile_Click", null!, null!);
            await Wait(() => window.OwnedWindows.Any());
            window.OwnedWindows.Single().Close(true);
            await Wait(() => Field(window, "_operationCancellation") == null);
            Check(profiles.SelectedItem == null && Field(window, "_session") == null && (await store.LoadAsync()).Count == 0, "Idle delete still removes only the isolated profile and disconnects");
            Check(File.Exists(databasePath), "Profile deletion does not delete the isolated database");
            Console.WriteLine("PASS actual Avalonia edit/delete/connect handlers, busy entry, dialog lease, cancellation and ownership");
            lifetime.Shutdown(0);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            lifetime.Shutdown(1);
        }
    })).StartWithClassicDesktopLifetime(args);

static object? Field(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);
static object? Invoke(object target, string name, params object[] values) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, values);
static void Check(bool success, string description)
{
    if (!success) throw new Exception(description);
    Console.WriteLine("PASS " + description);
}
static async Task Wait(Func<bool> predicate)
{
    for (int attempt = 0; attempt < 100; attempt++)
    {
        if (predicate()) return;
        await Task.Delay(50);
    }
    throw new TimeoutException("isolated UI step did not finish");
}
