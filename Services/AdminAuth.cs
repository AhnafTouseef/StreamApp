using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using MovieHub.Configuration;

namespace MovieHub.Services;

public static class AdminAuthDefaults
{
    public const string Scheme = "MovieHubAdmin";
}

/// <summary>PBKDF2-SHA256 password hashing. Format: v1.{iterations}.{saltBase64}.{hashBase64}</summary>
public static class PasswordHasher
{
    private const int Iterations = 210_000;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"v1.{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('.');
        if (parts.Length != 4 || parts[0] != "v1" || !int.TryParse(parts[1], out var iterations) || iterations < 10_000)
            return false;
        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public sealed class AdminAuthService
{
    private readonly AdminOptions _opt;
    private readonly ILogger<AdminAuthService> _log;

    public AdminAuthService(IOptions<AdminOptions> opt, ILogger<AdminAuthService> log)
    {
        _opt = opt.Value;
        _log = log;
        if (!_opt.IsConfigured)
            _log.LogWarning("Admin login is DISABLED: set Admin:PasswordHash (run 'dotnet run -- hash-password \"<password>\"') or Admin:Password.");
        else if (string.IsNullOrEmpty(_opt.PasswordHash))
            _log.LogWarning("Admin:Password is set in plain text. Use Admin:PasswordHash outside local development.");
    }

    public bool IsConfigured => _opt.IsConfigured;
    public string Username => _opt.Username;

    public bool Validate(string? username, string? password)
    {
        if (!_opt.IsConfigured || string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password)) return false;

        // Compare both fields in constant time and always evaluate both.
        var userOk = FixedEquals(username, _opt.Username);
        var passOk = !string.IsNullOrEmpty(_opt.PasswordHash)
            ? PasswordHasher.Verify(password, _opt.PasswordHash)
            : FixedEquals(password, _opt.Password);
        return userOk & passOk;
    }

    private static bool FixedEquals(string a, string b)
    {
        var ha = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(a));
        var hb = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(b));
        return CryptographicOperations.FixedTimeEquals(ha, hb);
    }
}

/// <summary>In-memory brute-force guard: 5 failures from one address lock login for 5 minutes.</summary>
public sealed class LoginThrottle
{
    private const int MaxFailures = 5;
    private static readonly TimeSpan LockFor = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, (int Failures, DateTime LockedUntil)> _state = new();

    public bool IsLocked(string key) =>
        _state.TryGetValue(key, out var s) && s.LockedUntil > DateTime.UtcNow;

    public void RecordFailure(string key) =>
        _state.AddOrUpdate(key,
            _ => (1, DateTime.MinValue),
            (_, s) =>
            {
                var failures = s.LockedUntil != DateTime.MinValue && s.LockedUntil <= DateTime.UtcNow ? 1 : s.Failures + 1;
                return failures >= MaxFailures ? (0, DateTime.UtcNow + LockFor) : (failures, DateTime.MinValue);
            });

    public void Reset(string key) => _state.TryRemove(key, out _);
}
