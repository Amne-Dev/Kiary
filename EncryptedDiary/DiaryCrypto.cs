using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace EncryptedDiary;

public static class DiaryCrypto
{
    private static readonly byte[] MagicHeader = Encoding.ASCII.GetBytes("EDRY1");

    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const int Pbkdf2Iterations = 210_000;

    public static byte[] Encrypt(string plaintext, string password)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new CryptographicException("Master password cannot be empty.");
        }

        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] key = DeriveKey(password, salt);
        byte[] plainBytes = Encoding.UTF8.GetBytes(plaintext);
        byte[] cipherBytes = new byte[plainBytes.Length];
        byte[] tag = new byte[TagSize];

        using (AesGcm aes = new(key, TagSize))
        {
            aes.Encrypt(nonce, plainBytes, cipherBytes, tag);
        }

        using MemoryStream stream = new();
        stream.Write(MagicHeader);
        stream.Write(salt);
        stream.Write(nonce);
        stream.Write(tag);
        stream.Write(cipherBytes);
        return stream.ToArray();
    }

    public static string Decrypt(byte[] payload, string password)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new CryptographicException("Master password cannot be empty.");
        }

        int minimumLength = MagicHeader.Length + SaltSize + NonceSize + TagSize;
        if (payload.Length < minimumLength)
        {
            throw new InvalidDataException("Encrypted file is incomplete.");
        }

        ReadOnlySpan<byte> raw = payload;
        if (!raw[..MagicHeader.Length].SequenceEqual(MagicHeader))
        {
            throw new InvalidDataException("Encrypted file header is invalid.");
        }

        int index = MagicHeader.Length;
        byte[] salt = raw.Slice(index, SaltSize).ToArray();
        index += SaltSize;
        byte[] nonce = raw.Slice(index, NonceSize).ToArray();
        index += NonceSize;
        byte[] tag = raw.Slice(index, TagSize).ToArray();
        index += TagSize;
        byte[] cipherBytes = raw[index..].ToArray();

        byte[] key = DeriveKey(password, salt);
        byte[] plaintext = new byte[cipherBytes.Length];
        using (AesGcm aes = new(key, TagSize))
        {
            aes.Decrypt(nonce, cipherBytes, tag, plaintext);
        }

        return Encoding.UTF8.GetString(plaintext);
    }

    private static byte[] DeriveKey(string password, byte[] salt)
    {
        return Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            Pbkdf2Iterations,
            HashAlgorithmName.SHA256,
            KeySize);
    }
}
