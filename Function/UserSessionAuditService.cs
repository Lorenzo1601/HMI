using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HMI.Function;

public sealed class UserSessionAuditService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _sync = new();
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private List<UserSessionAuditEntry> _entries = [];
    private string? _filePath;

    public async Task InitializeAsync(string filePath, int retentionDays)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var resolvedPath = Path.GetFullPath(filePath);
        var directory = Path.GetDirectoryName(resolvedPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        List<UserSessionAuditEntry> loadedEntries = [];
        if (File.Exists(resolvedPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(resolvedPath);
                loadedEntries = JsonSerializer.Deserialize<List<UserSessionAuditEntry>>(json, JsonOptions) ?? [];
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("Il registro delle sessioni utente e danneggiato o non valido.", exception);
            }
        }

        lock (_sync)
        {
            _filePath = resolvedPath;
            _entries = loadedEntries
                .OfType<UserSessionAuditEntry>()
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Id) && !string.IsNullOrWhiteSpace(entry.UserId))
                .ToList();
            PurgeCore(retentionDays, DateTime.UtcNow);
        }
        await SaveAsync();
    }

    public async Task<UserSessionAuditEntry> RecordLoginAsync(
        AuthenticatedUserIdentity user,
        string runtimeMode = "Runtime",
        DateTime? loggedInAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(user);
        var entry = new UserSessionAuditEntry
        {
            UserId = user.Id,
            Username = user.Username,
            DisplayName = user.DisplayName,
            AccessLevel = user.AccessLevel,
            LoggedInAtUtc = ToUtc(loggedInAtUtc ?? DateTime.UtcNow),
            ClientName = Environment.MachineName,
            RuntimeMode = string.IsNullOrWhiteSpace(runtimeMode) ? "Runtime" : runtimeMode.Trim()
        };
        lock (_sync)
        {
            _entries.Add(entry);
        }
        await SaveAsync();
        return entry.Clone();
    }

    public async Task<bool> RecordLogoutAsync(
        string sessionId,
        UserSessionEndReason reason = UserSessionEndReason.ManualLogout,
        DateTime? loggedOutAtUtc = null)
    {
        UserSessionAuditEntry? entry;
        lock (_sync)
        {
            entry = _entries.FirstOrDefault(item => item.Id == sessionId);
            if (entry is null || entry.LoggedOutAtUtc is not null)
            {
                return false;
            }
            var logoutAt = ToUtc(loggedOutAtUtc ?? DateTime.UtcNow);
            entry.LoggedOutAtUtc = logoutAt < entry.LoggedInAtUtc ? entry.LoggedInAtUtc : logoutAt;
            entry.EndReason = reason;
        }
        await SaveAsync();
        return true;
    }

    public async Task<int> CloseOpenSessionsAsync(
        UserSessionEndReason reason,
        DateTime? loggedOutAtUtc = null)
    {
        var logoutAt = ToUtc(loggedOutAtUtc ?? DateTime.UtcNow);
        var count = 0;
        lock (_sync)
        {
            foreach (var entry in _entries.Where(item => item.LoggedOutAtUtc is null))
            {
                entry.LoggedOutAtUtc = logoutAt < entry.LoggedInAtUtc ? entry.LoggedInAtUtc : logoutAt;
                entry.EndReason = reason;
                count++;
            }
        }
        if (count > 0)
        {
            await SaveAsync();
        }
        return count;
    }

    public IReadOnlyList<UserSessionAuditEntry> GetEntries()
    {
        lock (_sync)
        {
            return _entries
                .OrderByDescending(entry => entry.LoggedInAtUtc)
                .Select(entry => entry.Clone())
                .ToList();
        }
    }

    public async Task<int> PurgeAsync(int retentionDays)
    {
        int removed;
        lock (_sync)
        {
            removed = PurgeCore(retentionDays, DateTime.UtcNow);
        }
        if (removed > 0)
        {
            await SaveAsync();
        }
        return removed;
    }

    public Task FlushAsync() => SaveAsync();

    public static string BuildFilePath(string? projectPath, string projectName, bool runtimeOnly)
    {
        if (!runtimeOnly && !string.IsNullOrWhiteSpace(projectPath))
        {
            var fullProjectPath = Path.GetFullPath(projectPath);
            return Path.Combine(
                Path.GetDirectoryName(fullProjectPath)!,
                Path.GetFileNameWithoutExtension(fullProjectPath) + ".user-sessions.json");
        }

        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HMI Studio",
            "UserSessions");
        var safeName = MakeSafeFileName(projectName);
        var projectKey = string.IsNullOrWhiteSpace(projectPath)
            ? projectName
            : Path.GetFullPath(projectPath);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(projectKey ?? string.Empty));
        var suffix = Convert.ToHexString(digest.AsSpan(0, 8)).ToLowerInvariant();
        return Path.Combine(root, $"{safeName}-{suffix}.user-sessions.json");
    }

    private int PurgeCore(int retentionDays, DateTime nowUtc)
    {
        var cutoff = nowUtc.AddDays(-Math.Clamp(retentionDays, 1, 3650));
        return _entries.RemoveAll(entry =>
            (entry.LoggedOutAtUtc ?? entry.LoggedInAtUtc) < cutoff);
    }

    private async Task SaveAsync()
    {
        await _saveLock.WaitAsync();
        try
        {
            string? path;
            List<UserSessionAuditEntry> snapshot;
            lock (_sync)
            {
                path = _filePath;
                snapshot = _entries.Select(entry => entry.Clone()).ToList();
            }
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            var directory = Path.GetDirectoryName(path)
                ?? throw new InvalidOperationException("Percorso del registro sessioni non valido.");
            var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            try
            {
                var json = JsonSerializer.Serialize(snapshot, JsonOptions);
                await File.WriteAllTextAsync(temporaryPath, json);
                File.Move(temporaryPath, path, true);
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
                    // Un temporaneo residuo non deve bloccare le successive scritture del registro.
                }
            }
        }
        finally
        {
            _saveLock.Release();
        }
    }

    private static string MakeSafeFileName(string projectName)
    {
        var source = string.IsNullOrWhiteSpace(projectName) ? "progetto" : projectName.Trim();
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var safeName = string.Concat(source.Select(character => invalidCharacters.Contains(character) ? '_' : character))
            .Trim(' ', '.');
        if (safeName.Length == 0)
        {
            safeName = "progetto";
        }
        return safeName.Length <= 80 ? safeName : safeName[..80];
    }

    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}

public sealed class UserSessionAuditEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string UserId { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int AccessLevel { get; set; }
    public DateTime LoggedInAtUtc { get; set; }
    public DateTime? LoggedOutAtUtc { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public UserSessionEndReason? EndReason { get; set; }
    public string ClientName { get; set; } = string.Empty;
    public string RuntimeMode { get; set; } = string.Empty;

    public UserSessionAuditEntry Clone() => (UserSessionAuditEntry)MemberwiseClone();
}

public enum UserSessionEndReason
{
    ManualLogout,
    AutomaticTimeout,
    UserChanged,
    ReturnToDevelopment,
    ApplicationClosed,
    RuntimeExit,
    UserDeleted,
    UnexpectedTermination
}
