using System.IO;
using System.Windows;
using HMI.ExternalConnection;
using HMI.Function;

namespace HMI;

public partial class App : Application
{
    public static IMachineConnection? Connection { get; internal set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var baseDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        var manifestPath = Path.Combine(baseDirectory, RuntimePackageManifest.FileName);
        var fallbackRuntimeProject = Path.Combine(baseDirectory, "runtime.hmiproject");
        var manifest = RuntimeExportService.TryReadManifest(baseDirectory);
        Window mainWindow;
        if (manifest is not null)
        {
            var projectPath = Path.GetFullPath(Path.Combine(baseDirectory, manifest.ProjectFile));
            var packageRoot = baseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!projectPath.StartsWith(packageRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(projectPath))
            {
                MessageBox.Show("Il pacchetto runtime non è valido o il progetto runtime è mancante.", "Avvio runtime", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(-1);
                return;
            }
            mainWindow = new MainWindow(projectPath);
        }
        else if (File.Exists(fallbackRuntimeProject))
        {
            // Il progetto runtime è anche un marcatore fail-closed: rimuovere o
            // danneggiare il manifesto non deve mai rendere disponibile l'editor.
            mainWindow = new MainWindow(fallbackRuntimeProject);
        }
        else if (File.Exists(manifestPath))
        {
            MessageBox.Show("Il manifesto del pacchetto runtime è danneggiato.", "Avvio runtime", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
            return;
        }
        else
        {
            mainWindow = new MainWindow();
        }
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (Connection is not null)
        {
            await Connection.DisconnectAsync();
            Connection = null;
        }
        base.OnExit(e);
    }
}
