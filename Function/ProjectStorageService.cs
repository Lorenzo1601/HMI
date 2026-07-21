using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using HMI.Models;

namespace HMI.Function;

public sealed class ProjectStorageService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public async Task SaveAsync(HmiProject project, string path)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("Percorso del progetto non valido.");
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        await _saveLock.WaitAsync();
        try
        {
            project.Normalize();
            Directory.CreateDirectory(directory);
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, project, JsonOptions);
                await stream.FlushAsync();
            }
            File.Move(temporaryPath, fullPath, true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch
            {
                // Un file temporaneo residuo non deve bloccare ulteriori salvataggi.
            }
            finally
            {
                _saveLock.Release();
            }
        }
    }

    public async Task<HmiProject> LoadAsync(string path)
    {
        var json = await File.ReadAllTextAsync(path);
        var project = JsonSerializer.Deserialize<HmiProject>(json, JsonOptions)
            ?? throw new InvalidDataException("Il file non contiene un progetto HMI valido.");
        project.Normalize();
        return project;
    }
}
