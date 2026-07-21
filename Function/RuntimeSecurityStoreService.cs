using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HMI.Models;

namespace HMI.Function;

public sealed class RuntimeSecurityStoreService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly SemaphoreSlim _ioLock = new(1, 1);

    public async Task<SecuritySettings?> LoadAsync(
        string? projectPath,
        string projectName,
        CancellationToken cancellationToken = default)
    {
        var filePath = BuildFilePath(projectPath, projectName);
        await _ioLock.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(filePath))
            {
                return null;
            }

            try
            {
                await using var stream = new FileStream(
                    filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var settings = await JsonSerializer.DeserializeAsync<SecuritySettings>(stream, JsonOptions, cancellationToken);
                if (settings is null)
                {
                    TryQuarantineCorruptFile(filePath);
                    return null;
                }
                return CloneAndNormalize(settings);
            }
            catch (JsonException)
            {
                TryQuarantineCorruptFile(filePath);
                return null;
            }
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async Task<SecuritySettings> LoadOrCloneAsync(
        string? projectPath,
        string projectName,
        SecuritySettings projectFallback,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projectFallback);
        var merged = CloneAndNormalize(projectFallback);
        try
        {
            var localOverride = await LoadAsync(projectPath, projectName, cancellationToken);
            if (localOverride is not null)
            {
                merged.Users = localOverride.Users;
                merged = CloneAndNormalize(merged);
            }
            return merged;
        }
        catch (IOException)
        {
            return merged;
        }
        catch (UnauthorizedAccessException)
        {
            return merged;
        }
    }

    public async Task SaveAsync(
        string? projectPath,
        string projectName,
        SecuritySettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var filePath = BuildFilePath(projectPath, projectName);
        var directory = Path.GetDirectoryName(filePath)
            ?? throw new InvalidOperationException("Percorso archivio sicurezza runtime non valido.");
        var snapshot = CloneAndNormalize(settings);

        await _ioLock.WaitAsync(cancellationToken);
        string? temporaryPath = null;
        try
        {
            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporaryPath, filePath, true);
            temporaryPath = null;
        }
        finally
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(temporaryPath) && File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch
            {
                // La pulizia del temporaneo non deve lasciare il servizio permanentemente bloccato.
            }
            finally
            {
                _ioLock.Release();
            }
        }
    }

    public static string BuildFilePath(string? projectPath, string projectName)
    {
        var safeName = MakeSafeFileName(projectName);
        var normalizedProjectPath = string.IsNullOrWhiteSpace(projectPath)
            ? string.Empty
            : Path.GetFullPath(projectPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var identity = $"{normalizedProjectPath.ToUpperInvariant()}\n{projectName?.Trim().ToUpperInvariant()}";
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        var suffix = Convert.ToHexString(digest.AsSpan(0, 10)).ToLowerInvariant();
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HMI Studio",
            "RuntimeSecurity");
        return Path.Combine(root, $"{safeName}-{suffix}.security.json");
    }

    public static SecuritySettings CloneAndNormalize(SecuritySettings source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var json = JsonSerializer.Serialize(source, JsonOptions);
        var clone = JsonSerializer.Deserialize<SecuritySettings>(json, JsonOptions) ?? new SecuritySettings();

        clone.Users = clone.Users?.OfType<UserDefinition>().ToList() ?? [];
        clone.MaximumAccessLevel = Math.Clamp(clone.MaximumAccessLevel, 1, 1000);
        clone.AnonymousAccessLevel = Math.Clamp(clone.AnonymousAccessLevel, 0, clone.MaximumAccessLevel);
        clone.MinimumPasswordLength = Math.Clamp(clone.MinimumPasswordLength, 8, 128);
        clone.MaximumFailedLoginAttempts = Math.Clamp(clone.MaximumFailedLoginAttempts, 1, 20);
        clone.LoginLockoutMinutes = Math.Clamp(clone.LoginLockoutMinutes, 1, 24 * 60);
        clone.AutomaticLogoutMinutes = Math.Clamp(clone.AutomaticLogoutMinutes, 0, 24 * 60);
        clone.SessionHistoryRetentionDays = Math.Clamp(clone.SessionHistoryRetentionDays, 1, 3650);

        var userIds = new HashSet<string>(StringComparer.Ordinal);
        var usernames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < clone.Users.Count; index++)
        {
            var user = clone.Users[index];
            if (string.IsNullOrWhiteSpace(user.Id) || !userIds.Add(user.Id))
            {
                user.Id = Guid.NewGuid().ToString("N");
                userIds.Add(user.Id);
            }

            var baseUsername = string.IsNullOrWhiteSpace(user.Username)
                ? $"utente{index + 1}"
                : user.Username.Trim();
            var username = baseUsername;
            var suffix = 2;
            while (!usernames.Add(username))
            {
                username = $"{baseUsername}_{suffix++}";
            }

            user.Username = username;
            user.DisplayName = string.IsNullOrWhiteSpace(user.DisplayName) ? username : user.DisplayName.Trim();
            user.AccessLevel = Math.Clamp(user.AccessLevel, 0, clone.MaximumAccessLevel);
            user.PasswordAlgorithm = string.IsNullOrWhiteSpace(user.PasswordAlgorithm)
                ? UserDefinition.DefaultPasswordAlgorithm
                : user.PasswordAlgorithm.Trim();
            user.PasswordIterations = Math.Clamp(user.PasswordIterations, 10_000, 2_000_000);
            user.PasswordSalt = user.PasswordSalt?.Trim() ?? string.Empty;
            user.PasswordHash = user.PasswordHash?.Trim() ?? string.Empty;
            user.FailedLoginAttempts = Math.Clamp(user.FailedLoginAttempts, 0, clone.MaximumFailedLoginAttempts);
            if (user.LockedUntilUtc is DateTime lockedUntilUtc)
            {
                user.LockedUntilUtc = lockedUntilUtc.Kind switch
                {
                    DateTimeKind.Utc => lockedUntilUtc,
                    DateTimeKind.Local => lockedUntilUtc.ToUniversalTime(),
                    _ => DateTime.SpecifyKind(lockedUntilUtc, DateTimeKind.Utc)
                };
                if (user.LockedUntilUtc <= DateTime.UtcNow)
                {
                    user.LockedUntilUtc = null;
                    user.FailedLoginAttempts = 0;
                }
                else
                {
                    user.FailedLoginAttempts = clone.MaximumFailedLoginAttempts;
                }
            }
            if (user.CreatedAtUtc == default)
            {
                user.CreatedAtUtc = DateTime.UtcNow;
            }
        }

        return clone;
    }

    private static void TryQuarantineCorruptFile(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return;
            }
            var quarantinePath = $"{filePath}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
            File.Move(filePath, quarantinePath);
        }
        catch
        {
            // Anche se la quarantena non e possibile, l'avvio usa la sicurezza incorporata nel progetto.
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
}
