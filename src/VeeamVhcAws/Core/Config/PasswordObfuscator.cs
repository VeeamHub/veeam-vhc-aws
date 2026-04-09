using System.Security.Cryptography;
using System.Text;

namespace VeeamVhcAws.Core.Config;

public static class PasswordObfuscator
{
    private const string Prefix = "ENC:";
    private const string Passphrase = "veeam-vhc-aws-config-obfuscation-v1";
    private static readonly byte[] Salt = Encoding.UTF8.GetBytes("vhc-aws-salt-2024");
    private const int Iterations = 100_000;
    private const int KeySize = 32; // 256-bit
    private const int NonceSize = 12; // 96-bit GCM nonce
    private const int TagSize = 16; // 128-bit GCM tag

    private static readonly Lazy<byte[]> _key = new(() =>
        Rfc2898DeriveBytes.Pbkdf2(Passphrase, Salt, Iterations, HashAlgorithmName.SHA256, KeySize));

    public static string Obfuscate(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
            return plaintext;

        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_key.Value, TagSize);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        var combined = new byte[NonceSize + ciphertext.Length + TagSize];
        Buffer.BlockCopy(nonce, 0, combined, 0, NonceSize);
        Buffer.BlockCopy(ciphertext, 0, combined, NonceSize, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, combined, NonceSize + ciphertext.Length, TagSize);

        return Prefix + Convert.ToBase64String(combined);
    }

    public static string Deobfuscate(string value)
    {
        if (string.IsNullOrEmpty(value) || !value.StartsWith(Prefix))
            return value;

        try
        {
            var combined = Convert.FromBase64String(value[Prefix.Length..]);
            if (combined.Length < NonceSize + TagSize)
                throw new InvalidOperationException("Obfuscated value is too short.");

            var nonce = combined[..NonceSize];
            var tag = combined[^TagSize..];
            var ciphertext = combined[NonceSize..^TagSize];
            var plaintext = new byte[ciphertext.Length];

            using var aes = new AesGcm(_key.Value, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);

            return Encoding.UTF8.GetString(plaintext);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Password appears obfuscated (ENC: prefix) but cannot be decoded. " +
                "Re-run 'veeam-vhc-aws encrypt-config' or enter the plaintext password.", ex);
        }
    }
}
