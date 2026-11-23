using System;
using System.Security.Cryptography;
using System.Text;

namespace NE.ChatAI.Settings;

/// <summary>Hashes and verifies the application's optional network PIN and derives its session token.</summary>
public static class PinSecurity
{
    private const string Prefix = "v1";
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int TokenBytes = 32;
    private const int Iterations = 100_000;

    /// <summary>Hashes <paramref name="pin"/> with PBKDF2-SHA256 and embeds a fresh 256-bit random session token.</summary>
    public static string Hash(string pin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pin);

        var trimmed = pin.Trim();
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(trimmed), salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        var token = RandomNumberGenerator.GetBytes(TokenBytes);

        return $"{Prefix}:{Convert.ToHexStringLower(salt)}:{Convert.ToHexStringLower(hash)}:{Convert.ToHexStringLower(token)}";
    }

    /// <summary>Checks whether <paramref name="candidate"/> matches <paramref name="storedHash"/> in constant time.</summary>
    public static bool Verify(string? candidate, string? storedHash)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(storedHash))
            return false;

        var parts = storedHash.Split(':');

        if (parts.Length < 3 || !string.Equals(parts[0], Prefix, StringComparison.Ordinal))
            return false;

        byte[] salt;
        byte[] expected;

        try
        {
            salt = Convert.FromHexString(parts[1]);
            expected = Convert.FromHexString(parts[2]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (salt.Length != SaltBytes || expected.Length != HashBytes)
            return false;

        var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(candidate.Trim()), salt, Iterations, HashAlgorithmName.SHA256, expected.Length);

        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>Returns the 32-byte session token bound to <paramref name="storedHash"/>.</summary>
    public static byte[] TokenBytesOf(string storedHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storedHash);

        var parts = storedHash.Split(':');

        if (parts.Length >= 4 && string.Equals(parts[0], Prefix, StringComparison.Ordinal) && parts[3].Length == TokenBytes * 2)
        {
            try
            {
                return Convert.FromHexString(parts[3]);
            }
            catch (FormatException)
            {
            }
        }

        return SHA256.HashData(Encoding.UTF8.GetBytes("NE.ChatAI.PinToken:" + storedHash));
    }
}
