using System.Security.Cryptography;
using System.Text;

namespace VeeamVhcAws.Core.Config;

public static class PasswordObfuscator
{
    private const string Prefix = "ENC:";
    private static readonly byte[] PayloadMagic = "VHCE"u8.ToArray();
    private const byte PayloadVersion = 1;
    private const string LegacyPassphrase = "veeam-vhc-aws-config-obfuscation-v1";
    private static readonly byte[] LegacySalt = Encoding.UTF8.GetBytes("vhc-aws-salt-2024");
    private const int Iterations = 100_000;
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private static readonly Lazy<byte[]> LegacyKey = new(() =>
        Rfc2898DeriveBytes.Pbkdf2(LegacyPassphrase, LegacySalt, Iterations, HashAlgorithmName.SHA256, KeySize));

    public static string Obfuscate(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
            return plaintext;

        var body = Seal(SecretKeyStore.CurrentKey(), plaintext, localFormat: true);
        return Prefix + Convert.ToBase64String(body);
    }

    public static string Deobfuscate(string value)
    {
        if (string.IsNullOrEmpty(value) || !value.StartsWith(Prefix, StringComparison.Ordinal))
            return value;

        try
        {
            var combined = Convert.FromBase64String(value[Prefix.Length..]);
            if (IsLocalPayload(combined))
                return Open(SecretKeyStore.CurrentKey(), combined, localFormat: true);
            return Open(LegacyKey.Value, combined, localFormat: false);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                "Password appears protected (ENC: prefix) but cannot be decoded. " +
                "Re-run 'veeam-vhc-aws encrypt-config' or enter the plaintext password.", ex);
        }
    }

    internal static bool IsLegacyCiphertext(string value)
    {
        if (string.IsNullOrEmpty(value) || !value.StartsWith(Prefix, StringComparison.Ordinal))
            return false;
        try
        {
            var combined = Convert.FromBase64String(value[Prefix.Length..]);
            return !IsLocalPayload(combined);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool IsLocalPayload(byte[] combined) =>
        combined.Length > PayloadMagic.Length + 1 + NonceSize + TagSize
        && combined.AsSpan(0, PayloadMagic.Length).SequenceEqual(PayloadMagic)
        && combined[PayloadMagic.Length] == PayloadVersion;

    private static byte[] Seal(byte[] key, string plaintext, bool localFormat)
    {
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        var header = localFormat ? PayloadMagic.Length + 1 : 0;
        var combined = new byte[header + NonceSize + ciphertext.Length + TagSize];
        var offset = 0;
        if (localFormat)
        {
            Buffer.BlockCopy(PayloadMagic, 0, combined, 0, PayloadMagic.Length);
            combined[PayloadMagic.Length] = PayloadVersion;
            offset = header;
        }
        Buffer.BlockCopy(nonce, 0, combined, offset, NonceSize);
        Buffer.BlockCopy(ciphertext, 0, combined, offset + NonceSize, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, combined, offset + NonceSize + ciphertext.Length, TagSize);
        return combined;
    }

    private static string Open(byte[] key, byte[] combined, bool localFormat)
    {
        var offset = localFormat ? PayloadMagic.Length + 1 : 0;
        if (combined.Length < offset + NonceSize + TagSize)
            throw new InvalidOperationException("Protected value is too short.");

        var nonce = combined[offset..(offset + NonceSize)];
        var tag = combined[^TagSize..];
        var ciphertext = combined[(offset + NonceSize)..^TagSize];
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return Encoding.UTF8.GetString(plaintext);
    }
}
