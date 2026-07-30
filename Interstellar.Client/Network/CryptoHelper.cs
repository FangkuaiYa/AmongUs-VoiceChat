using System;
using System.Security.Cryptography;

namespace Interstellar.Network;

/// <summary>
/// AES-256-GCM application-layer encryption matching the Go server format.
/// Frame format: [2 bytes: total_len][12 bytes: nonce][ciphertext + 16 bytes: GCM tag]
/// </summary>
internal static class CryptoHelper
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;

    private static AesGcm? _aesGcm;
    private static readonly object _lock = new();

    /// <summary>Initialize with a 32-byte key. Pass null or empty to disable encryption.</summary>
    public static void Initialize(byte[]? key)
    {
        lock (_lock)
        {
            _aesGcm?.Dispose();
            _aesGcm = null;

            if (key == null || key.Length == 0) return;
            if (key.Length != KeySize)
            {
                VoiceChatPlugin.InterstellarPlugin.Logger?.LogWarning(
                    $"[VC:Crypto] Key must be 32 bytes, got {key.Length} — encryption disabled.");
                return;
            }
            _aesGcm = new AesGcm(key);
            VoiceChatPlugin.InterstellarPlugin.Logger?.LogInfo("[VC:Crypto] AES-256-GCM encryption enabled.");
        }
    }

    /// <summary>Whether encryption is active.</summary>
    public static bool IsEnabled
    {
        get { lock (_lock) return _aesGcm != null; }
    }

    /// <summary>Encrypt a plaintext frame. Returns the encrypted frame ready for WebSocket send.</summary>
    public static byte[] EncryptFrame(byte[] plaintext)
    {
        lock (_lock)
        {
            if (_aesGcm == null) return plaintext;

            var nonce = new byte[NonceSize];
            RandomNumberGenerator.Fill(nonce);

            int cipherLen = plaintext.Length + TagSize;
            var ciphertext = new byte[NonceSize + cipherLen];

            // Copy nonce
            System.Buffer.BlockCopy(nonce, 0, ciphertext, 0, NonceSize);

            // Encrypt
            _aesGcm.Encrypt(
                nonce,
                plaintext,
                ciphertext.AsSpan(NonceSize, plaintext.Length),  // destination for ciphertext
                ciphertext.AsSpan(NonceSize + plaintext.Length, TagSize)); // destination for tag

            // Prepend 2-byte total length
            var result = new byte[2 + ciphertext.Length];
            result[0] = (byte)(ciphertext.Length >> 8);
            result[1] = (byte)ciphertext.Length;
            System.Buffer.BlockCopy(ciphertext, 0, result, 2, ciphertext.Length);

            return result;
        }
    }

    /// <summary>Decrypt an encrypted frame. Returns the plaintext payload.</summary>
    public static byte[]? DecryptFrame(byte[] encrypted)
    {
        lock (_lock)
        {
            if (_aesGcm == null) return encrypted;

            if (encrypted.Length < 2 + NonceSize + TagSize)
                return null;

            // Read total length (big-endian)
            int totalLen = (encrypted[0] << 8) | encrypted[1];
            if (2 + totalLen != encrypted.Length)
                return null;

            var nonce = new byte[NonceSize];
            System.Buffer.BlockCopy(encrypted, 2, nonce, 0, NonceSize);

            int payloadLen = totalLen - NonceSize - TagSize;
            if (payloadLen <= 0) return null;

            var plaintext = new byte[payloadLen];
            int cipherOffset = 2 + NonceSize;
            int tagOffset = cipherOffset + payloadLen;

            try
            {
                _aesGcm.Decrypt(
                    nonce,
                    encrypted.AsSpan(cipherOffset, payloadLen),
                    encrypted.AsSpan(tagOffset, TagSize),
                    plaintext);
                return plaintext;
            }
            catch (CryptographicException)
            {
                return null; // wrong key or tampered data
            }
        }
    }

    /// <summary>Parse a 64-character hex string into a 32-byte key.</summary>
    public static byte[]? ParseHexKey(string hex)
    {
        if (string.IsNullOrEmpty(hex)) return null;
        if (hex.Length != 64)
        {
            VoiceChatPlugin.InterstellarPlugin.Logger?.LogWarning(
                $"[VC:Crypto] Hex key must be 64 chars (32 bytes), got {hex.Length}.");
            return null;
        }
        try
        {
            var key = new byte[32];
            for (int i = 0; i < 32; i++)
                key[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return key;
        }
        catch
        {
            VoiceChatPlugin.InterstellarPlugin.Logger?.LogWarning("[VC:Crypto] Invalid hex key.");
            return null;
        }
    }
}
