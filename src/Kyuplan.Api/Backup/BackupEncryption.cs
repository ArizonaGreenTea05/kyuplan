using System.Security.Cryptography;
using System.Text;
using Kyuplan.Api.Persistence;

namespace Kyuplan.Api.Backup;

/// <summary>
/// Optional passphrase protection of a backup file: <c>KYUENC1</c>, salt (16), nonce (12), tag (16), then the zip encrypted with AES-256-GCM under a key
/// derived with PBKDF2-SHA256. A wrong passphrase or a damaged file fails the tag check, so it is never read as data.
/// </summary>
public static class BackupEncryption
{
    private static readonly byte[] Magic = "KYUENC1"u8.ToArray();
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int Iterations = 600_000;

    public static bool IsEncrypted(ReadOnlySpan<byte> file) => file.StartsWith(Magic);

    public static byte[] Encrypt(byte[] plain, string passphrase)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var tag = new byte[TagSize];
        var cipher = new byte[plain.Length];

        using (var aes = new AesGcm(Key(passphrase, salt), TagSize)) aes.Encrypt(nonce, plain, cipher, tag, Magic);

        return [.. Magic, .. salt, .. nonce, .. tag, .. cipher];
    }

    /// <summary>Throws <see cref="InvalidInputException"/> when the passphrase is wrong or the file is damaged.</summary>
    public static byte[] Decrypt(byte[] file, string passphrase)
    {
        var header = Magic.Length + SaltSize + NonceSize + TagSize;
        if (file.Length < header) throw new InvalidInputException("This is no readable backup file: it is cut off.");

        var salt = file.AsSpan(Magic.Length, SaltSize);
        var nonce = file.AsSpan(Magic.Length + SaltSize, NonceSize);
        var tag = file.AsSpan(Magic.Length + SaltSize + NonceSize, TagSize);
        var cipher = file.AsSpan(header);
        var plain = new byte[cipher.Length];

        try
        {
            using var aes = new AesGcm(Key(passphrase, salt), TagSize);
            aes.Decrypt(nonce, cipher, tag, plain, Magic);
        }
        catch (CryptographicException)
        {
            throw new InvalidInputException("The passphrase is wrong, or the backup file is damaged.");
        }

        return plain;
    }

    private static byte[] Key(string passphrase, ReadOnlySpan<byte> salt) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase), salt, Iterations, HashAlgorithmName.SHA256, 32);
}
