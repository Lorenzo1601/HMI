using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using HMI.Models;

namespace HMI.Function;

public sealed class RuntimeExportService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task ExportAsync(HmiProject sourceProject, string zipPath, string? localPanelId)
    {
        var project = CloneProject(sourceProject);
        if (!string.IsNullOrWhiteSpace(localPanelId))
        {
            foreach (var panel in project.Redundancy.Panels)
            {
                panel.IsLocal = panel.Id == localPanelId;
            }
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "HMIStudioRuntimeExport");
        var packageDirectory = Path.Combine(tempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageDirectory);
        try
        {
            CopyRuntimeFiles(AppContext.BaseDirectory, packageDirectory);
            var projectFile = Path.Combine(packageDirectory, "runtime.hmiproject");
            await new ProjectStorageService().SaveAsync(project, projectFile);

            var manifest = new RuntimePackageManifest
            {
                ProjectFile = "runtime.hmiproject",
                ProjectName = project.Name,
                ExportedAtUtc = DateTime.UtcNow
            };
            await File.WriteAllTextAsync(
                Path.Combine(packageDirectory, RuntimePackageManifest.FileName),
                JsonSerializer.Serialize(manifest, JsonOptions));
            await File.WriteAllTextAsync(
                Path.Combine(packageDirectory, "LEGGIMI_RUNTIME.txt"),
                $"{project.Name}\r\n\r\nAvviare HMI.exe. Questo pacchetto apre esclusivamente il runtime operatore.\r\nRichiede .NET 10 Desktop Runtime per Windows.\r\nEsportato il {DateTime.Now:dd/MM/yyyy HH:mm}.\r\n");

            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }
            ZipFile.CreateFromDirectory(packageDirectory, zipPath, CompressionLevel.Optimal, false);
        }
        finally
        {
            var resolvedPackage = Path.GetFullPath(packageDirectory);
            var resolvedRoot = Path.GetFullPath(tempRoot) + Path.DirectorySeparatorChar;
            if (resolvedPackage.StartsWith(resolvedRoot, StringComparison.OrdinalIgnoreCase) && Directory.Exists(resolvedPackage))
            {
                Directory.Delete(resolvedPackage, true);
            }
        }
    }

    public static RuntimePackageManifest? TryReadManifest(string baseDirectory)
    {
        var path = Path.Combine(baseDirectory, RuntimePackageManifest.FileName);
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<RuntimePackageManifest>(File.ReadAllText(path), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static HmiProject CloneProject(HmiProject source)
    {
        var json = JsonSerializer.Serialize(source, JsonOptions);
        var clone = JsonSerializer.Deserialize<HmiProject>(json, JsonOptions) ?? HmiProject.CreateStarter();
        clone.Normalize();
        foreach (var user in clone.Security.Users)
        {
            user.FailedLoginAttempts = 0;
            user.LockedUntilUtc = null;
        }
        return clone;
    }

    private static void CopyRuntimeFiles(string sourceDirectory, string destinationDirectory)
    {
        foreach (var sourceFile in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var extension = Path.GetExtension(sourceFile);
            var name = Path.GetFileName(sourceFile);
            if (extension.Equals(".pdb", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".hmiproject", StringComparison.OrdinalIgnoreCase) ||
                name.Equals(RuntimePackageManifest.FileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var relative = Path.GetRelativePath(sourceDirectory, sourceFile);
            var destination = Path.Combine(destinationDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(sourceFile, destination, true);
        }
    }
}

public sealed class RuntimePackageManifest
{
    public const string FileName = "runtime-package.json";
    public string ProjectFile { get; set; } = "runtime.hmiproject";
    public string ProjectName { get; set; } = string.Empty;
    public DateTime ExportedAtUtc { get; set; }
}
