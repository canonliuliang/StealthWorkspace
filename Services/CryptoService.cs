using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace StealthWorkspace.Services;

/// <summary>
/// File format supported by the encrypted media vault.
/// V1 = legacy layout produced by the first implementation (no AAD, no length header).
/// V2 = current layout: authenticated header, authenticated per-chunk index, fixed
///      record stride (so the file can be decrypted with random access) and a
///      per-file key derived with HKDF from the vault master key.
/// </summary>
public enum VaultFileFormat
{
    LegacyV1 = 1,
    V2 = 2
}

public sealed class VaultFileHeader
{
    public VaultFileFormat Format { get; init; }
    public int ChunkSize { get; init; }
    public long PlainLength { get; init; }
    public byte[] FileSalt { get; init; } = Array.Empty<byte>();
    public long DataOffset { get; init; }
    public int ChunkCount { get; init; }

    /// <summary>Explicit record table for legacy V1 files (which have no length header);
    /// null for V2 where record offsets follow the fixed stride formula.</summary>
    public (long Offset, int Length)[]? ChunkTable { get; init; }

    public long RecordStride => CryptoService.RecordHeaderSize + (long)ChunkSize;
}

/// <summary>
/// AES-256-GCM chunked file encryption for the media vault.
///
/// Threat model notes:
///  - Every chunk is independently authenticated (GCM tag).
///  - V2 additionally authenticates the chunk index, total length, chunk size and the
///    per-file salt as AAD, so chunks cannot be silently reordered, dropped or truncated.
///  - V2 derives a per-file key with HKDF from the vault master key, so a single
///    file's key material never equals the vault key.
///  - Decrypted plaintext lives only in memory buffers that are released/zeroed as soon
///    as possible; no plaintext temporary file is ever produced during playback.
/// </summary>
public static class CryptoService
{
    public const int KeySize = 32;              // 256-bit AES key
    public const int SaltSize = 16;
    public const int NonceSize = 12;            // 96-bit GCM nonce
    public const int TagSize = 16;              // 128-bit GCM tag
    public const int DefaultChunkSize = 1024 * 1024; // 1 MiB
    public const int MaxChunkSize = 8 * 1024 * 1024;
    public const int MinChunkSize = 64 * 1024;
    public const int DefaultIterations = 600_000;
    public const int LegacyIterations = 100_000;
    public const int RecordHeaderSize = NonceSize + TagSize + sizeof(int); // 32
    public const int HeaderSizeV1 = 8 + SaltSize + sizeof(int);            // 28
    public const int HeaderSizeV2 = 8 + sizeof(int) + sizeof(int) + sizeof(long) + SaltSize; // 40

    private static readonly byte[] MagicV1 = Encoding.ASCII.GetBytes("SVAULT01");
    private static readonly byte[] MagicV2 = Encoding.ASCII.GetBytes("SVAULT02");
    private static readonly byte[] V2KeyInfo = Encoding.ASCII.GetBytes("StealthWorkspace.Vault.FileKey.v2");

    #region Key derivation

    public static byte[] DeriveKey(string password, byte[] salt, int iterations = DefaultIterations)
    {
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("Password must not be empty.", nameof(password));
        if (salt == null || salt.Length < 8) throw new ArgumentException("Salt is too short.", nameof(salt));
        iterations = Math.Clamp(iterations, 10_000, 10_000_000);
        return Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, KeySize);
    }

    public static byte[] HashKey(byte[] key) => SHA256.HashData(key);

    public static bool VerifyKeyHash(byte[] key, string expectedHex)
    {
        if (key == null || string.IsNullOrEmpty(expectedHex)) return false;
        try
        {
            byte[] expected = Convert.FromHexString(expectedHex);
            if (expected.Length != 32) return false;
            return CryptographicOperations.FixedTimeEquals(HashKey(key), expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static byte[] GenerateRandomBytes(int count)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        byte[] bytes = new byte[count];
        RandomNumberGenerator.Fill(bytes);
        return bytes;
    }

    internal static byte[] DeriveFileKey(byte[] masterKey, byte[] fileSalt)
    {
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, KeySize, fileSalt, V2KeyInfo);
    }

    #endregion

    #region Header

    /// <summary>Reads and validates the header of an encrypted vault file.</summary>
    public static VaultFileHeader ReadHeader(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ReadHeader(stream);
    }

    public static VaultFileHeader ReadHeader(Stream stream)
    {
        stream.Position = 0;
        return ReadHeader(stream, stream.Length, CancellationToken.None);
    }

    // A ZIP entry is forward-only. Its uncompressed length is supplied by the caller.
    internal static VaultFileHeader ReadHeader(Stream stream, long encryptedLength, CancellationToken token)
    {
        if (encryptedLength < HeaderSizeV1) throw new InvalidDataException("保险箱文件头不完整。");
        Span<byte> header = stackalloc byte[HeaderSizeV2];
        ReadExactly(stream, header[..8]);
        if (header[..8].SequenceEqual(MagicV1))
        {
            ReadExactly(stream, header[8..HeaderSizeV1]);
            return ReadHeaderV1(stream, header[..HeaderSizeV1], encryptedLength, token);
        }
        if (!header[..8].SequenceEqual(MagicV2)) throw new InvalidDataException("无效的保险箱文件格式。");
        ReadExactly(stream, header[8..]);
        return ReadHeaderV2(header, encryptedLength);
    }

    private static VaultFileHeader ReadHeaderV1(Stream stream, ReadOnlySpan<byte> buffer, long fileLength, CancellationToken token)
    {
        int chunkSize = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(8 + SaltSize, 4));
        if (chunkSize < MinChunkSize || chunkSize > MaxChunkSize)
            throw new InvalidDataException("保险箱文件的分块大小无效。");

        byte[] salt = buffer.Slice(8, SaltSize).ToArray();
        long dataOffset = HeaderSizeV1;

        // V1 has no length header: walk the record headers once to build the chunk table.
        long plainTotal = 0;
        long pos = dataOffset;
        var table = stream.CanSeek ? new List<(long Offset, int Length)>() : null;
        byte[]? skipBuffer = stream.CanSeek ? null : new byte[64 * 1024];
        int count = 0;
        Span<byte> record = stackalloc byte[RecordHeaderSize];

        while (pos < fileLength)
        {
            if (fileLength - pos < RecordHeaderSize) throw new InvalidDataException("保险箱文件块头不完整。");
            token.ThrowIfCancellationRequested();
            ReadExactly(stream, record);
            int cipherLen = BinaryPrimitives.ReadInt32LittleEndian(record[(NonceSize + TagSize)..]);
            if (cipherLen <= 0 || cipherLen > chunkSize) throw new InvalidDataException("保险箱文件块长度无效。");
            long next = pos + RecordHeaderSize + cipherLen;
            if (next > fileLength) throw new InvalidDataException("保险箱文件被截断。");
            table?.Add((pos, cipherLen));
            if (stream.CanSeek) stream.Position = next;
            else
            {
                for (int left = cipherLen; left > 0;)
                {
                    token.ThrowIfCancellationRequested();
                    int block = Math.Min(left, skipBuffer!.Length);
                    ReadExactly(stream, skipBuffer.AsSpan(0, block));
                    left -= block;
                }
            }
            pos = next;
            plainTotal += cipherLen;
            if (++count > 4_000_000) throw new InvalidDataException("保险箱文件块数量异常。");
        }

        if (pos != fileLength) throw new InvalidDataException("保险箱文件结构损坏。");

        return new VaultFileHeader
        {
            Format = VaultFileFormat.LegacyV1,
            ChunkSize = chunkSize,
            PlainLength = plainTotal,
            FileSalt = salt,
            DataOffset = dataOffset,
            ChunkCount = count,
            ChunkTable = table?.ToArray()
        };
    }

    private static VaultFileHeader ReadHeaderV2(ReadOnlySpan<byte> buffer, long encryptedLength)
    {
        int version = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(8, 4));
        if (version != 2) throw new InvalidDataException("不支持的保险箱文件版本。");

        int chunkSize = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(12, 4));
        if (chunkSize < MinChunkSize || chunkSize > MaxChunkSize)
            throw new InvalidDataException("保险箱文件的分块大小无效。");

        long plainLength = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(16, 8));
        if (plainLength < 0) throw new InvalidDataException("保险箱文件长度无效。");

        byte[] salt = buffer.Slice(24, SaltSize).ToArray();
        long chunkCount = plainLength == 0 ? 0 : 1 + (plainLength - 1) / chunkSize;
        if (chunkCount > int.MaxValue || encryptedLength - HeaderSizeV2 - chunkCount * RecordHeaderSize != plainLength)
            throw new InvalidDataException("保险箱文件长度与头部不一致（可能被截断或篡改）。");

        return new VaultFileHeader
        {
            Format = VaultFileFormat.V2,
            ChunkSize = chunkSize,
            PlainLength = plainLength,
            FileSalt = salt,
            DataOffset = HeaderSizeV2,
            ChunkCount = (int)chunkCount
        };
    }

    internal static byte[] BuildAad(ReadOnlySpan<byte> magic, long plainLength, int chunkSize, ReadOnlySpan<byte> fileSalt, int chunkIndex, int cipherLength)
    {
        byte[] aad = new byte[8 + 8 + 4 + SaltSize + 4 + 4];
        magic.CopyTo(aad);
        BinaryPrimitives.WriteInt64LittleEndian(aad.AsSpan(8), plainLength);
        BinaryPrimitives.WriteInt32LittleEndian(aad.AsSpan(16), chunkSize);
        fileSalt.CopyTo(aad.AsSpan(20));
        BinaryPrimitives.WriteInt32LittleEndian(aad.AsSpan(20 + SaltSize), chunkIndex);
        BinaryPrimitives.WriteInt32LittleEndian(aad.AsSpan(20 + SaltSize + 4), cipherLength);
        return aad;
    }

    #endregion

    #region Encryption

    /// <summary>
    /// Encrypts <paramref name="sourceFilePath"/> into a new V2 vault file at
    /// <paramref name="destEncryptedPath"/>. The destination is written from scratch;
    /// callers should write to a temporary name and move it into place afterwards.
    /// </summary>
    public static void EncryptFile(
        string sourceFilePath,
        string destEncryptedPath,
        byte[] masterKey,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var source = new FileStream(sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, FileOptions.SequentialScan);
        EncryptStream(source, destEncryptedPath, masterKey, progress, cancellationToken);
    }

    internal static void EncryptStream(Stream input, string destEncryptedPath, byte[] masterKey,
        IProgress<double>? progress, CancellationToken cancellationToken, IncrementalHash? digest = null)
    {
        if (masterKey == null || masterKey.Length != KeySize) throw new ArgumentException("Invalid vault key.", nameof(masterKey));

        long plainLength = input.Length - input.Position;
        if (plainLength < 0) throw new InvalidDataException("源文件位置超出文件长度。");
        byte[] fileSalt = GenerateRandomBytes(SaltSize);
        byte[] fileKey = DeriveFileKey(masterKey, fileSalt);
        byte[] plainBuffer = Array.Empty<byte>();
        byte[] cipherBuffer = Array.Empty<byte>();

        try
        {
            using var output = new FileStream(destEncryptedPath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 64 * 1024, FileOptions.SequentialScan);

            output.Write(MagicV2);
            Span<byte> header = stackalloc byte[HeaderSizeV2 - 8];
            BinaryPrimitives.WriteInt32LittleEndian(header.Slice(0, 4), 2);
            BinaryPrimitives.WriteInt32LittleEndian(header.Slice(4, 4), DefaultChunkSize);
            BinaryPrimitives.WriteInt64LittleEndian(header.Slice(8, 8), plainLength);
            fileSalt.CopyTo(header.Slice(16, SaltSize));
            output.Write(header);

            using var aes = new AesGcm(fileKey, TagSize);
            plainBuffer = new byte[DefaultChunkSize];
            cipherBuffer = new byte[DefaultChunkSize];
            byte[] nonce = new byte[NonceSize];
            byte[] tag = new byte[TagSize];

            long processed = 0;
            int index = 0;
            Span<byte> lenBytes = stackalloc byte[sizeof(int)];
            while (processed < plainLength)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int toRead = (int)Math.Min(DefaultChunkSize, plainLength - processed);
                int read = ReadAtLeast(input, plainBuffer, toRead);
                if (read != toRead) throw new IOException("源文件在加密过程中被修改或截断。");
                digest?.AppendData(plainBuffer, 0, read);

                RandomNumberGenerator.Fill(nonce);
                byte[] aad = BuildAad(MagicV2, plainLength, DefaultChunkSize, fileSalt, index, read);
                aes.Encrypt(nonce, plainBuffer.AsSpan(0, read), cipherBuffer.AsSpan(0, read), tag, aad);

                output.Write(nonce);
                output.Write(tag);
                BinaryPrimitives.WriteInt32LittleEndian(lenBytes, read);
                output.Write(lenBytes);
                output.Write(cipherBuffer, 0, read);

                processed += read;
                index++;
                progress?.Report(plainLength == 0 ? 1.0 : (double)processed / plainLength);
            }

            output.Flush(true);

            long expected = HeaderSizeV2 + (long)index * RecordHeaderSize + plainLength;
            if (output.Length != expected) throw new IOException("加密文件长度校验失败。");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainBuffer);
            CryptographicOperations.ZeroMemory(cipherBuffer);
            CryptographicOperations.ZeroMemory(fileKey);
        }
    }

    /// <summary>
    /// Re-encrypts a vault file with a different master key without ever writing a
    /// plaintext temporary file (used when the user changes the vault password).
    /// </summary>
    public static void ReEncryptFile(
        string sourceEncryptedPath,
        string destEncryptedPath,
        byte[] oldMasterKey,
        byte[] newMasterKey,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (oldMasterKey == null || oldMasterKey.Length != KeySize) throw new ArgumentException("Invalid old key.", nameof(oldMasterKey));
        if (newMasterKey == null || newMasterKey.Length != KeySize) throw new ArgumentException("Invalid new key.", nameof(newMasterKey));

        using var input = new FileStream(sourceEncryptedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        VaultFileHeader header = ReadHeader(input);
        byte[] oldFileKey = header.Format == VaultFileFormat.V2 ? DeriveFileKey(oldMasterKey, header.FileSalt) : (byte[])oldMasterKey.Clone();

        byte[] newSalt = GenerateRandomBytes(SaltSize);
        byte[] newFileKey = DeriveFileKey(newMasterKey, newSalt);

        try
        {
            using var output = new FileStream(destEncryptedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            output.Write(MagicV2);
            Span<byte> headerBytes = stackalloc byte[HeaderSizeV2 - 8];
            BinaryPrimitives.WriteInt32LittleEndian(headerBytes.Slice(0, 4), 2);
            BinaryPrimitives.WriteInt32LittleEndian(headerBytes.Slice(4, 4), header.ChunkSize);
            BinaryPrimitives.WriteInt64LittleEndian(headerBytes.Slice(8, 8), header.PlainLength);
            newSalt.CopyTo(headerBytes.Slice(16, SaltSize));
            output.Write(headerBytes);

            using var oldAes = new AesGcm(oldFileKey, TagSize);
            using var newAes = new AesGcm(newFileKey, TagSize);

            var offsets = BuildOffsets(header);
            byte[] plainBuffer = new byte[header.ChunkSize];
            byte[] cipherScratch = new byte[header.ChunkSize];
            byte[] cipherBuffer = new byte[header.ChunkSize];
            byte[] nonce = new byte[NonceSize];
            byte[] tag = new byte[TagSize];

            long processed = 0;
            Span<byte> lenBytes = stackalloc byte[sizeof(int)];
            for (int index = 0; index < offsets.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int plainLen = DecryptChunkInto(input, oldAes, header, index, plainBuffer, cipherScratch);

                RandomNumberGenerator.Fill(nonce);
                byte[] newAad = BuildAad(MagicV2, header.PlainLength, header.ChunkSize, newSalt, index, plainLen);
                newAes.Encrypt(nonce, plainBuffer.AsSpan(0, plainLen), cipherBuffer.AsSpan(0, plainLen), tag, newAad);

                output.Write(nonce);
                output.Write(tag);
                BinaryPrimitives.WriteInt32LittleEndian(lenBytes, plainLen);
                output.Write(lenBytes);
                output.Write(cipherBuffer, 0, plainLen);

                processed += plainLen;
                progress?.Report(header.PlainLength == 0 ? 1.0 : (double)processed / header.PlainLength);
            }

            CryptographicOperations.ZeroMemory(plainBuffer);
            CryptographicOperations.ZeroMemory(cipherScratch);
            CryptographicOperations.ZeroMemory(cipherBuffer);
            output.Flush(true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(oldFileKey);
            CryptographicOperations.ZeroMemory(newFileKey);
        }
    }

    #endregion

    #region Decryption

    /// <summary>Decrypts a vault file back to a local file (export).</summary>
    public static void DecryptToFile(
        string encryptedFilePath,
        string targetDecryptedPath,
        byte[] masterKey,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (masterKey == null || masterKey.Length != KeySize) throw new ArgumentException("Invalid vault key.", nameof(masterKey));

        using var input = new FileStream(encryptedFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        VaultFileHeader header = ReadHeader(input);
        byte[] fileKey = header.Format == VaultFileFormat.V2 ? DeriveFileKey(masterKey, header.FileSalt) : (byte[])masterKey.Clone();

        try
        {
            using var output = new FileStream(targetDecryptedPath, FileMode.Create, FileAccess.Write, FileShare.None);
            using var aes = new AesGcm(fileKey, TagSize);

            var offsets = BuildOffsets(header);
            byte[] plainBuffer = new byte[header.ChunkSize];
            byte[] cipherScratch = new byte[header.ChunkSize];
            long processed = 0;

            for (int index = 0; index < offsets.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int plainLen = DecryptChunkInto(input, aes, header, index, plainBuffer, cipherScratch);
                output.Write(plainBuffer, 0, plainLen);
                processed += plainLen;
                progress?.Report(header.PlainLength == 0 ? 1.0 : (double)processed / header.PlainLength);
            }

            CryptographicOperations.ZeroMemory(plainBuffer);
            CryptographicOperations.ZeroMemory(cipherScratch);
            output.Flush(true);

            if (processed != header.PlainLength)
                throw new InvalidDataException("解密长度与文件头不一致。");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(fileKey);
        }
    }

    /// <summary>
    /// Decrypts a whole vault file into memory. Only intended for small payloads
    /// (images/thumbnails); video playback must use <see cref="OpenReadStream"/>.
    /// </summary>
    public static MemoryStream DecryptToMemoryStream(
        string encryptedFilePath,
        byte[] masterKey,
        long maxBytes = 256L * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        VaultFileHeader header = ReadHeader(encryptedFilePath);
        if (header.PlainLength > maxBytes)
            throw new InvalidDataException("文件过大，无法整体载入内存。");

        var memory = new MemoryStream(capacity: (int)Math.Min(header.PlainLength, 64L * 1024 * 1024));
        try
        {
            using var input = new FileStream(encryptedFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            byte[] fileKey = header.Format == VaultFileFormat.V2 ? DeriveFileKey(masterKey, header.FileSalt) : (byte[])masterKey.Clone();
            try
            {
                using var aes = new AesGcm(fileKey, TagSize);
                var offsets = BuildOffsets(header);
                byte[] plainBuffer = new byte[header.ChunkSize];
                byte[] cipherScratch = new byte[header.ChunkSize];
                for (int index = 0; index < offsets.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int plainLen = DecryptChunkInto(input, aes, header, index, plainBuffer, cipherScratch);
                    memory.Write(plainBuffer, 0, plainLen);
                }
                CryptographicOperations.ZeroMemory(plainBuffer);
                CryptographicOperations.ZeroMemory(cipherScratch);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(fileKey);
            }

            memory.Position = 0;
            return memory;
        }
        catch
        {
            memory.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens a seekable, on-demand decrypting stream over a vault file. Media players
    /// can seek randomly without ever materialising the whole plaintext in memory or
    /// on disk. Wrap with <c>stream.AsRandomAccessStream()</c> for WinRT media APIs.
    /// </summary>
    public static Stream OpenReadStream(string encryptedFilePath, byte[] masterKey)
    {
        return new VaultMediaStream(encryptedFilePath, masterKey);
    }

    private static List<(long Offset, int CipherLength)> BuildOffsets(VaultFileHeader header)
    {
        if (header.ChunkTable != null)
            return new List<(long, int)>(header.ChunkTable);

        var offsets = new List<(long, int)>(header.ChunkCount);
        long expectedFullChunks = header.PlainLength / header.ChunkSize;
        int remainder = (int)(header.PlainLength % header.ChunkSize);

        long position = header.DataOffset;
        for (int i = 0; i < header.ChunkCount; i++)
        {
            int cipherLength = i < expectedFullChunks ? header.ChunkSize : remainder;
            if (cipherLength <= 0)
                throw new InvalidDataException("保险箱文件块长度无效。");
            offsets.Add((position, cipherLength));
            position += RecordHeaderSize + cipherLength;
        }

        return offsets;
    }

    /// <summary>
    /// Decrypts chunk <paramref name="index"/> into <paramref name="destination"/> and
    /// returns the number of plaintext bytes produced.
    /// </summary>
    internal static int DecryptChunkInto(
        FileStream input,
        AesGcm aes,
        VaultFileHeader header,
        int index,
        byte[] destination,
        byte[] cipherScratch)
    {
        if ((uint)index >= (uint)header.ChunkCount) throw new ArgumentOutOfRangeException(nameof(index));

        long recordOffset;
        int expectedCipherLength;
        if (header.ChunkTable != null)
        {
            (recordOffset, expectedCipherLength) = header.ChunkTable[index];
        }
        else
        {
            recordOffset = header.DataOffset + index * header.RecordStride;
            long plainBefore = (long)index * header.ChunkSize;
            expectedCipherLength = (int)Math.Min(header.ChunkSize, header.PlainLength - plainBefore);
        }

        if (expectedCipherLength <= 0 || expectedCipherLength > header.ChunkSize)
            throw new InvalidDataException("保险箱文件块长度与头部不一致。");
        if (destination.Length < expectedCipherLength || cipherScratch.Length < expectedCipherLength)
            throw new InvalidDataException("解密缓冲区过小。");

        input.Position = recordOffset;
        Span<byte> nonce = stackalloc byte[NonceSize];
        Span<byte> tag = stackalloc byte[TagSize];
        Span<byte> lenBytes = stackalloc byte[sizeof(int)];
        ReadExactly(input, nonce);
        ReadExactly(input, tag);
        ReadExactly(input, lenBytes);

        int cipherLength = BinaryPrimitives.ReadInt32LittleEndian(lenBytes);
        if (cipherLength != expectedCipherLength)
            throw new InvalidDataException("保险箱文件块长度与头部不一致。");

        ReadExactly(input, cipherScratch.AsSpan(0, cipherLength));
        Span<byte> plain = destination.AsSpan(0, cipherLength);
        if (header.Format == VaultFileFormat.V2)
        {
            byte[] aad = BuildAad(MagicV2, header.PlainLength, header.ChunkSize, header.FileSalt, index, cipherLength);
            aes.Decrypt(nonce, cipherScratch.AsSpan(0, cipherLength), tag, plain, aad);
        }
        else
        {
            aes.Decrypt(nonce, cipherScratch.AsSpan(0, cipherLength), tag, plain);
        }

        return cipherLength;
    }

    #endregion

    #region Shredding

    /// <summary>
    /// Overwrites and removes a regular, singly linked file through one exclusive handle.
    /// Reparse points and hard links are retained. Failed overwrite never falls back
    /// to ordinary deletion. Physical erasure remains best-effort on SSD/COW storage.
    /// </summary>
    public static bool ShredFile(string filePath, int? passCount = null) => ShredFile(filePath, passCount, workspace: false);

    internal static bool ShredWorkspaceFile(string filePath, int passCount) => ShredFile(filePath, passCount, workspace: true);

    private static bool ShredFile(string filePath, int? passCount, bool workspace)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return false;
        try { return SafeShred.OverwriteAndDelete(filePath, Math.Clamp(passCount ?? GetConfiguredPasses(), 1, 7), workspace: workspace); }
        catch (Exception ex)
        {
            AppLog.Warn($"粉碎文件失败，未执行普通删除: {ex.GetType().Name}");
            return false;
        }
    }

    internal static bool ShredImportedFile(string filePath, SafeShred.FileIdentity expected, byte[] digest,
        Func<bool>? authorize = null, bool workspace = false)
    {
        try { return SafeShred.OverwriteAndDelete(filePath, GetConfiguredPasses(), expected, digest, authorize, workspace); }
        catch (Exception ex)
        {
            AppLog.Warn($"导入后的原件已改变或无法安全粉碎，原件已保留: {ex.GetType().Name}");
            return false;
        }
    }

    private static int GetConfiguredPasses()
    {
        try
        {
            return SettingsService.Instance.Data.ShredPasses;
        }
        catch
        {
            return 3;
        }
    }

    #endregion

    #region Helpers

    internal static void ReadExactly(Stream stream, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = stream.Read(buffer.Slice(total));
            if (read <= 0) throw new EndOfStreamException("文件在读取过程中意外结束。");
            total += read;
        }
    }

    private static int ReadAtLeast(Stream stream, byte[] buffer, int count)
    {
        int total = 0;
        while (total < count)
        {
            int read = stream.Read(buffer, total, count - total);
            if (read <= 0) break;
            total += read;
        }
        return total;
    }

    #endregion
}
