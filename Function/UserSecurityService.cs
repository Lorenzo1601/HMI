using System.Security.Cryptography;
using HMI.Models;

namespace HMI.Function;

public sealed class UserSecurityService
{
    private const int MaximumUsernameLength = 64;
    private const int MaximumDisplayNameLength = 128;
    private readonly object _authenticationSync = new();

    public UserDefinition CreateUser(
        SecuritySettings settings,
        string username,
        string displayName,
        int accessLevel,
        bool isActive,
        string password)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Users ??= [];

        var normalizedUsername = ValidateUsername(username);
        EnsureUniqueUsername(settings, normalizedUsername, null);
        ValidateAccessLevel(settings, accessLevel);

        var user = new UserDefinition
        {
            Username = normalizedUsername,
            DisplayName = NormalizeDisplayName(displayName, normalizedUsername),
            AccessLevel = accessLevel,
            IsActive = isActive,
            CreatedAtUtc = DateTime.UtcNow
        };
        PasswordHashingService.SetPassword(user, password, settings.MinimumPasswordLength);
        settings.Users.Add(user);
        return user;
    }

    public UserDefinition UpdateUser(
        SecuritySettings settings,
        string userId,
        string username,
        string displayName,
        int accessLevel,
        bool isActive)
    {
        var user = FindRequiredUser(settings, userId);
        var normalizedUsername = ValidateUsername(username);
        EnsureUniqueUsername(settings, normalizedUsername, user.Id);
        ValidateAccessLevel(settings, accessLevel);
        var normalizedDisplayName = NormalizeDisplayName(displayName, normalizedUsername);

        user.Username = normalizedUsername;
        user.DisplayName = normalizedDisplayName;
        user.AccessLevel = accessLevel;
        user.IsActive = isActive;
        return user;
    }

    public bool DeleteUser(SecuritySettings settings, string userId)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Users ??= [];
        var user = settings.Users.FirstOrDefault(item => item.Id == userId);
        if (user is null || !settings.Users.Remove(user))
        {
            return false;
        }
        return true;
    }

    public void ChangePassword(SecuritySettings settings, string userId, string newPassword)
    {
        var user = FindRequiredUser(settings, userId);
        PasswordHashingService.SetPassword(user, newPassword, settings.MinimumPasswordLength);
        ResetLoginLockout(user);
    }

    public UserAuthenticationResult Authenticate(SecuritySettings settings, string username, string password)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Users ??= [];

        if (string.IsNullOrWhiteSpace(username) || password is null)
        {
            return UserAuthenticationResult.Failed(AuthenticationFailureReason.InvalidCredentials);
        }

        var user = settings.Users.FirstOrDefault(item =>
            string.Equals(item.Username, username.Trim(), StringComparison.OrdinalIgnoreCase));
        if (user is null)
        {
            return UserAuthenticationResult.Failed(AuthenticationFailureReason.InvalidCredentials);
        }
        if (!user.IsActive)
        {
            return UserAuthenticationResult.Failed(AuthenticationFailureReason.InactiveAccount);
        }
        var nowUtc = DateTime.UtcNow;
        var lockedUntilUtc = GetLockedUntilUtc(user, nowUtc);
        if (lockedUntilUtc is not null)
        {
            return UserAuthenticationResult.Failed(AuthenticationFailureReason.AccountLocked, lockedUntilUtc);
        }
        if (string.IsNullOrWhiteSpace(user.PasswordHash) || string.IsNullOrWhiteSpace(user.PasswordSalt))
        {
            return UserAuthenticationResult.Failed(AuthenticationFailureReason.PasswordNotConfigured);
        }
        if (!PasswordHashingService.VerifyPassword(user, password))
        {
            lockedUntilUtc = RegisterFailedLogin(settings, user, nowUtc);
            if (lockedUntilUtc is not null)
            {
                return UserAuthenticationResult.Failed(AuthenticationFailureReason.AccountLocked, lockedUntilUtc);
            }
            return UserAuthenticationResult.Failed(AuthenticationFailureReason.InvalidCredentials);
        }

        ResetLoginLockout(user);
        return UserAuthenticationResult.Success(
            new AuthenticatedUserIdentity(user.Id, user.Username, user.DisplayName, user.AccessLevel),
            PasswordHashingService.NeedsRehash(user));
    }

    public bool HasAccess(AuthenticatedUserIdentity? user, SecuritySettings settings, int requiredAccessLevel)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.Enabled)
        {
            return true;
        }
        var required = Math.Clamp(requiredAccessLevel, 0, Math.Max(1, settings.MaximumAccessLevel));
        var currentLevel = user?.AccessLevel ?? Math.Clamp(
            settings.AnonymousAccessLevel,
            0,
            Math.Max(1, settings.MaximumAccessLevel));
        return currentLevel >= required;
    }

    public DateTime? GetLoginLockoutUntilUtc(SecuritySettings settings, string userId)
    {
        var user = FindRequiredUser(settings, userId);
        return GetLockedUntilUtc(user, DateTime.UtcNow);
    }

    public void ResetLoginLockout(SecuritySettings settings, string userId)
    {
        var user = FindRequiredUser(settings, userId);
        ResetLoginLockout(user);
    }

    private void ResetLoginLockout(UserDefinition user)
    {
        lock (_authenticationSync)
        {
            user.FailedLoginAttempts = 0;
            user.LockedUntilUtc = null;
        }
    }

    private DateTime? RegisterFailedLogin(SecuritySettings settings, UserDefinition user, DateTime nowUtc)
    {
        var maximumAttempts = Math.Clamp(settings.MaximumFailedLoginAttempts, 1, 20);
        var lockoutMinutes = Math.Clamp(settings.LoginLockoutMinutes, 1, 24 * 60);
        lock (_authenticationSync)
        {
            if (user.LockedUntilUtc is not null && ToUtc(user.LockedUntilUtc.Value) <= nowUtc)
            {
                user.FailedLoginAttempts = 0;
                user.LockedUntilUtc = null;
            }

            user.FailedLoginAttempts = Math.Clamp(user.FailedLoginAttempts + 1, 0, maximumAttempts);
            if (user.FailedLoginAttempts < maximumAttempts)
            {
                return null;
            }

            user.LockedUntilUtc = nowUtc.AddMinutes(lockoutMinutes);
            return user.LockedUntilUtc;
        }
    }

    private DateTime? GetLockedUntilUtc(UserDefinition user, DateTime nowUtc)
    {
        lock (_authenticationSync)
        {
            if (user.LockedUntilUtc is null)
            {
                return null;
            }
            var lockedUntilUtc = ToUtc(user.LockedUntilUtc.Value);
            user.LockedUntilUtc = lockedUntilUtc;
            if (lockedUntilUtc > nowUtc)
            {
                return lockedUntilUtc;
            }

            user.FailedLoginAttempts = 0;
            user.LockedUntilUtc = null;
            return null;
        }
    }

    private static UserDefinition FindRequiredUser(SecuritySettings settings, string userId)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Users ??= [];
        return settings.Users.FirstOrDefault(item => item.Id == userId)
            ?? throw new KeyNotFoundException("Utente non trovato.");
    }

    private static void EnsureUniqueUsername(SecuritySettings settings, string username, string? exceptUserId)
    {
        if (settings.Users.Any(item =>
                item.Id != exceptUserId &&
                string.Equals(item.Username, username, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Esiste gia un utente con questo nome.");
        }
    }

    private static string ValidateUsername(string username)
    {
        var normalized = username?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Il nome utente e obbligatorio.", nameof(username));
        }
        if (normalized.Length > MaximumUsernameLength)
        {
            throw new ArgumentException($"Il nome utente puo contenere al massimo {MaximumUsernameLength} caratteri.", nameof(username));
        }
        if (normalized.Any(char.IsControl))
        {
            throw new ArgumentException("Il nome utente contiene caratteri non validi.", nameof(username));
        }
        return normalized;
    }

    private static string NormalizeDisplayName(string displayName, string fallback)
    {
        var normalized = string.IsNullOrWhiteSpace(displayName) ? fallback : displayName.Trim();
        if (normalized.Length > MaximumDisplayNameLength)
        {
            throw new ArgumentException($"Il nome visualizzato puo contenere al massimo {MaximumDisplayNameLength} caratteri.", nameof(displayName));
        }
        if (normalized.Any(char.IsControl))
        {
            throw new ArgumentException("Il nome visualizzato contiene caratteri non validi.", nameof(displayName));
        }
        return normalized;
    }

    private static void ValidateAccessLevel(SecuritySettings settings, int accessLevel)
    {
        var maximum = Math.Max(1, settings.MaximumAccessLevel);
        if (accessLevel < 0 || accessLevel > maximum)
        {
            throw new ArgumentOutOfRangeException(nameof(accessLevel), $"Il livello di accesso deve essere compreso tra 0 e {maximum}.");
        }
    }

    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}

public static class PasswordHashingService
{
    private const int SaltSizeBytes = 16;
    private const int HashSizeBytes = 32;
    private const int MaximumPasswordLength = 1024;

    public static void SetPassword(UserDefinition user, string password, int minimumLength = 8)
    {
        ArgumentNullException.ThrowIfNull(user);
        ValidatePassword(password, minimumLength);

        var salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            UserDefinition.DefaultPasswordIterations,
            HashAlgorithmName.SHA256,
            HashSizeBytes);

        user.PasswordAlgorithm = UserDefinition.DefaultPasswordAlgorithm;
        user.PasswordIterations = UserDefinition.DefaultPasswordIterations;
        user.PasswordSalt = Convert.ToBase64String(salt);
        user.PasswordHash = Convert.ToBase64String(hash);
        user.PasswordChangedAtUtc = DateTime.UtcNow;
    }

    public static bool VerifyPassword(UserDefinition user, string password)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (password is null || password.Length > MaximumPasswordLength ||
            !TryReadCredential(user, out var salt, out var expectedHash))
        {
            return false;
        }

        try
        {
            var actualHash = Rfc2898DeriveBytes.Pbkdf2(
                password,
                salt,
                user.PasswordIterations,
                HashAlgorithmName.SHA256,
                expectedHash.Length);
            return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
        }
        catch (FormatException)
        {
            return false;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    public static bool HasValidCredential(UserDefinition user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return TryReadCredential(user, out _, out _);
    }

    public static bool NeedsRehash(UserDefinition user)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (!string.Equals(user.PasswordAlgorithm, UserDefinition.DefaultPasswordAlgorithm, StringComparison.OrdinalIgnoreCase) ||
            user.PasswordIterations < UserDefinition.DefaultPasswordIterations)
        {
            return true;
        }
        try
        {
            return Convert.FromBase64String(user.PasswordSalt).Length < SaltSizeBytes ||
                   Convert.FromBase64String(user.PasswordHash).Length != HashSizeBytes;
        }
        catch (FormatException)
        {
            return true;
        }
    }

    public static void ValidatePassword(string password, int minimumLength = 8)
    {
        if (password is null)
        {
            throw new ArgumentNullException(nameof(password));
        }
        var minimum = Math.Clamp(minimumLength, 8, 128);
        if (password.Length < minimum)
        {
            throw new ArgumentException($"La password deve contenere almeno {minimum} caratteri.", nameof(password));
        }
        if (password.Length > MaximumPasswordLength)
        {
            throw new ArgumentException($"La password puo contenere al massimo {MaximumPasswordLength} caratteri.", nameof(password));
        }
    }

    private static bool TryReadCredential(UserDefinition user, out byte[] salt, out byte[] hash)
    {
        salt = [];
        hash = [];
        if (!string.Equals(user.PasswordAlgorithm, UserDefinition.DefaultPasswordAlgorithm, StringComparison.OrdinalIgnoreCase) ||
            user.PasswordIterations is < 10_000 or > 2_000_000)
        {
            return false;
        }
        try
        {
            salt = Convert.FromBase64String(user.PasswordSalt);
            hash = Convert.FromBase64String(user.PasswordHash);
            return salt.Length >= SaltSizeBytes && hash.Length is >= 16 and <= 128;
        }
        catch (FormatException)
        {
            salt = [];
            hash = [];
            return false;
        }
    }
}

public sealed record AuthenticatedUserIdentity(
    string Id,
    string Username,
    string DisplayName,
    int AccessLevel);

public sealed record UserAuthenticationResult(
    bool Succeeded,
    AuthenticatedUserIdentity? User,
    AuthenticationFailureReason FailureReason,
    bool PasswordRehashRecommended)
{
    public DateTime? LockedUntilUtc { get; init; }

    public static UserAuthenticationResult Success(AuthenticatedUserIdentity user, bool rehashRecommended) =>
        new(true, user, AuthenticationFailureReason.None, rehashRecommended);

    public static UserAuthenticationResult Failed(AuthenticationFailureReason reason, DateTime? lockedUntilUtc = null) =>
        new(false, null, reason, false) { LockedUntilUtc = lockedUntilUtc };
}

public enum AuthenticationFailureReason
{
    None,
    InvalidCredentials,
    InactiveAccount,
    PasswordNotConfigured,
    AccountLocked
}
