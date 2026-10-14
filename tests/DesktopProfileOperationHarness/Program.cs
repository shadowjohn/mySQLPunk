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
    command.CommandText = "CREATE TABLE fixture (id INTEGER PRIMARY KEY, label TEXT); INSERT INTO fixture VALUES (1, 'synthetic');";
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
            var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Func<CancellationToken, Task> pendingSql = async token => { await barrier.Task; token.ThrowIfCancellationRequested(); };
            var pending = (Task)Invoke(window, "RunOperationAsync", "synthetic pending SQL", pendingSql)!;
            foreach (var handler in new[] { "EditProfile_Click", "DeleteProfile_Click", "ConnectProfile_Click" })
                Invoke(window, handler, null!, null!);
            Check(!window.OwnedWindows.Any() && ReferenceEquals(session, Field(window, "_session")), "Busy actual handlers reject edit/delete/connect without dialogs or disconnect");
            ((CancellationTokenSource)Field(window, "_operationCancellation")!).Cancel();
            Invoke(window, "EditProfile_Click", null!, null!);
            Check(Field(window, "_operationCancellation") != null && !window.OwnedWindows.Any(), "Cancel retains real lease until pending operation completes");
            barrier.SetResult();
            await pending;
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
