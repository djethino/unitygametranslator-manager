using System.Security.Cryptography;
using System.Text;

namespace UnityGameTranslator.Installer.Core.Security;

/// <summary>
/// At-rest obfuscation for secrets, byte-for-byte compatible with the mod's TokenProtection.
///
/// ⚠ MIRROR OF UnityGameTranslator.Core/TokenProtection.cs. Every constant below — the prefix,
/// the iteration count, the salt source string, the "_UGT_v3" suffix — is part of the key
/// derivation. Change one and this side can no longer read what the mod wrote, or worse, writes
/// something the mod silently fails to decrypt. If that file changes, this one changes with it,
/// and RoundTripWorks() is what catches a drift before a user does.
///
/// Honest threat model, and it must not be oversold:
///   * it protects a file copied off the machine — a support share, a cloud sync, an accidental
///     commit — because the key cannot be rebuilt elsewhere;
///   * it protects nothing against a local process running as the same user, which can rebuild
///     the key from the same public identity values.
/// It is obfuscation bound to a machine, not a security boundary. The real defence for an API
/// key is revoking it at the provider.
/// </summary>
public static class SecretProtection
{
    private const string EncryptedPrefix = "ENCRYPTED:";

    /// <summary>Wraps a secret for storage. Returns null for null, empty for empty.</summary>
    public static string? Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return plain;
        return EncryptedPrefix + Encrypt(plain);
    }

    /// <summary>
    /// Reads a stored secret back. Returns null when it cannot be decrypted — which happens
    /// legitimately when the file was written on another machine, and the caller should treat it
    /// as "no secret" rather than as corruption.
    /// </summary>
    public static string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return stored;
        if (!stored.StartsWith(EncryptedPrefix, StringComparison.Ordinal)) return stored;

        try
        {
            return Decrypt(stored[EncryptedPrefix.Length..]);
        }
        catch
        {
            return null;
        }
    }

    public static bool IsProtected(string? value) =>
        value?.StartsWith(EncryptedPrefix, StringComparison.Ordinal) == true;

    /// <summary>
    /// Encrypts a probe value and reads it back.
    ///
    /// Called before announcing that a secret was saved. Two implementations of the same scheme
    /// live in two repositories; the failure mode of a drift is a config the mod cannot read,
    /// discovered by a player rather than by us. Verifying we can re-read what we just wrote
    /// costs milliseconds and catches it here.
    /// </summary>
    public static bool RoundTripWorks()
    {
        try
        {
            const string probe = "UGT round-trip probe";
            return Unprotect(Protect(probe)) == probe;
        }
        catch
        {
            return false;
        }
    }

    private static string Encrypt(string plainText)
    {
        var key = DeriveKey(MachineSecret(), MachineSalt());

        using var aes = Aes.Create();
        aes.Key = key;
        aes.GenerateIV();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var encryptor = aes.CreateEncryptor();
        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var encrypted = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

        // IV first, then the ciphertext — the layout the mod reads.
        var result = new byte[aes.IV.Length + encrypted.Length];
        Buffer.BlockCopy(aes.IV, 0, result, 0, aes.IV.Length);
        Buffer.BlockCopy(encrypted, 0, result, aes.IV.Length, encrypted.Length);

        return Convert.ToBase64String(result);
    }

    private static string Decrypt(string encryptedText)
    {
        var key = DeriveKey(MachineSecret(), MachineSalt());
        var combined = Convert.FromBase64String(encryptedText);

        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        var iv = new byte[16];
        Buffer.BlockCopy(combined, 0, iv, 0, 16);
        aes.IV = iv;

        var encrypted = new byte[combined.Length - 16];
        Buffer.BlockCopy(combined, 16, encrypted, 0, encrypted.Length);

        using var decryptor = aes.CreateDecryptor();
        var plainBytes = decryptor.TransformFinalBlock(encrypted, 0, encrypted.Length);
        return Encoding.UTF8.GetString(plainBytes);
    }

    /// <summary>
    /// PBKDF2 with 100,000 iterations, SHA-1 as the HMAC.
    ///
    /// SHA-1 is not a choice, it is a constraint carried over: the mod targets .NET Standard 2.0
    /// where Rfc2898DeriveBytes has no hash-algorithm parameter. Using SHA-256 here would be
    /// stronger and would produce a different key, which is exactly the drift this file must not
    /// introduce. It moves when the mod moves, together.
    /// </summary>
    private static byte[] DeriveKey(string secret, byte[] salt)
    {
#pragma warning disable SYSLIB0041 // iteration count is fine; the algorithm is pinned deliberately
        using var pbkdf2 = new Rfc2898DeriveBytes(secret, salt, 100000);
#pragma warning restore SYSLIB0041
        return pbkdf2.GetBytes(32);
    }

    /// <summary>
    /// The machine identity the key is derived from. Every value must be stable across runs —
    /// String.GetHashCode() is not, which is why the actual paths are used.
    /// </summary>
    private static string MachineSecret()
    {
        var builder = new StringBuilder();
        builder.Append(Environment.MachineName);
        builder.Append('_');
        builder.Append(Environment.UserName);
        builder.Append('_');
        builder.Append(Environment.OSVersion.Platform);
        builder.Append("_UGT_v3");

        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home))
            {
                builder.Append('_');
                builder.Append(home);
            }
        }
        catch
        {
            // A home directory we cannot read simply drops out of the secret, exactly as it does
            // on the mod's side — the two must fail identically or they derive different keys.
        }

        return builder.ToString();
    }

    private static byte[] MachineSalt()
    {
        var source = $"{Environment.MachineName}_{Environment.UserName}_UGT_SALT";
        return SHA256.HashData(Encoding.UTF8.GetBytes(source));
    }
}
