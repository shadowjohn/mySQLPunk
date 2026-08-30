using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MySqlPunk.Core.Services;

namespace MySqlPunk.Desktop;

public sealed partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = new MainWindow(SqlDocumentService.ResolveLaunchPath(desktop.Args));
            desktop.MainWindow = mainWindow;
            if (this.TryGetFeature<IActivatableLifetime>() is { } activatableLifetime)
            {
                activatableLifetime.Activated += async (_, eventArgs) =>
                {
                    if (eventArgs is FileActivatedEventArgs files)
                    {
                        await mainWindow.OpenActivatedSqlFilesAsync(files.Files);
                    }
                };
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
