using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using HMI.Models;

namespace HMI.Function;

public sealed class AlarmHistoryService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _sync = new();
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private List<AlarmHistoryEntry> _entries = [];
    private string? _filePath;

    public async Task InitializeAsync(string filePath, int retentionDays)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
        List<AlarmHistoryEntry> loadedEntries = [];
        if (File.Exists(filePath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(filePath);
                loadedEntries = JsonSerializer.Deserialize<List<AlarmHistoryEntry>>(json, JsonOptions) ?? [];
            }
            catch
            {
                loadedEntries = [];
            }
        }
        lock (_sync)
        {
            _filePath = filePath;
            _entries = loadedEntries;
        }
        Purge(retentionDays);
        await SaveAsync();
    }

    public AlarmHistoryEntry RecordActivation(AlarmDefinition alarm, DateTime activatedAtUtc)
    {
        var entry = new AlarmHistoryEntry
        {
            AlarmId = alarm.Id,
            AlarmName = alarm.Name,
            FolderId = alarm.FolderId,
            Severity = alarm.Severity,
            Message = alarm.Message,
            ActivatedAtUtc = activatedAtUtc
        };
        lock (_sync)
        {
            _entries.Add(entry);
        }
        _ = SaveAsync();
        return entry;
    }

    public void RecordResolution(string occurrenceId, DateTime resolvedAtUtc)
    {
        lock (_sync)
        {
            var entry = _entries.FirstOrDefault(item => item.Id == occurrenceId);
            if (entry is not null && entry.ResolvedAtUtc is null)
            {
                entry.ResolvedAtUtc = resolvedAtUtc;
            }
        }
        _ = SaveAsync();
    }

    public void RecordAcknowledgement(string occurrenceId, DateTime acknowledgedAtUtc)
    {
        lock (_sync)
        {
            var entry = _entries.FirstOrDefault(item => item.Id == occurrenceId);
            if (entry is not null)
            {
                entry.AcknowledgedAtUtc = acknowledgedAtUtc;
            }
        }
        _ = SaveAsync();
    }

    public IReadOnlyList<AlarmHistoryEntry> GetEntries()
    {
        lock (_sync)
        {
            return _entries.Select(entry => entry.Clone()).ToList();
        }
    }

    public void Purge(int retentionDays)
    {
        var cutoff = DateTime.UtcNow.AddDays(-Math.Clamp(retentionDays, 1, 3650));
        lock (_sync)
        {
            _entries.RemoveAll(entry => entry.ResolvedAtUtc is not null && entry.ResolvedAtUtc < cutoff);
        }
    }

    public async Task FlushAsync() => await SaveAsync();

    public static string BuildFilePath(string? projectPath, string projectName)
    {
        if (!string.IsNullOrWhiteSpace(projectPath))
        {
            var fullProjectPath = Path.GetFullPath(projectPath);
            return Path.Combine(Path.GetDirectoryName(fullProjectPath)!, Path.GetFileNameWithoutExtension(fullProjectPath) + ".alarm-history.json");
        }
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HMI Studio", "AlarmHistory");
        var safeName = string.Concat(projectName.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        return Path.Combine(root, safeName + ".alarm-history.json");
    }

    private async Task SaveAsync()
    {
        string? path;
        List<AlarmHistoryEntry> snapshot;
        lock (_sync)
        {
            path = _filePath;
            snapshot = _entries.Select(entry => entry.Clone()).ToList();
        }
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }
        await _saveLock.WaitAsync();
        try
        {
            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            var temporaryPath = path + ".tmp";
            await File.WriteAllTextAsync(temporaryPath, json);
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            _saveLock.Release();
        }
    }
}

public sealed class AlarmHistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string AlarmId { get; set; } = string.Empty;
    public string AlarmName { get; set; } = string.Empty;
    public string FolderId { get; set; } = string.Empty;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AlarmSeverity Severity { get; set; }
    public string Message { get; set; } = string.Empty;
    public DateTime ActivatedAtUtc { get; set; }
    public DateTime? ResolvedAtUtc { get; set; }
    public DateTime? AcknowledgedAtUtc { get; set; }

    public AlarmHistoryEntry Clone() => (AlarmHistoryEntry)MemberwiseClone();
}
