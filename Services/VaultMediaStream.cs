using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace StealthWorkspace.Services;

/// <summary>
/// Seekable, read-only <see cref="Stream"/> over an encrypted vault file that decrypts
/// chunks on demand. This is what allows 4K/high-bitrate media to play without ever
/// materialising the whole file in RAM (the previous implementation decrypted entire
/// videos into memory and could easily throw OutOfMemoryException) and without writing
/// any plaintext to disk.
/// Wrap with <c>AsRandomAccessStream()</c> when passing to WinRT media APIs.
/// </summary>
public sealed class VaultMediaStream : Stream
{
    private const int MaxCachedChunks = 4;

    private readonly FileStream _file;
    private readonly VaultFileHeader _header;
    private readonly byte[] _fileKey;
    private readonly AesGcm _aes;
    private readonly object _gate = new();

    private readonly Dictionary<int, byte[]> _cache = new();
    private readonly Queue<int> _cacheOrder = new();
    private byte[]? _cipherScratch;
    private long _position;
    private bool _disposed;

    public VaultMediaStream(string encryptedFilePath, byte[] masterKey)
    {
        if (masterKey == null || masterKey.Length != CryptoService.KeySize)
            throw new ArgumentException("Invalid vault key.", nameof(masterKey));

        _file = new FileStream(encryptedFilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, FileOptions.RandomAccess);
        try
        {
            _header = CryptoService.ReadHeader(_file);
            _fileKey = _header.Format == VaultFileFormat.V2
                ? CryptoService.DeriveFileKey(masterKey, _header.FileSalt)
                : (byte[])masterKey.Clone();
            _aes = new AesGcm(_fileKey, CryptoService.TagSize);
        }
        catch
        {
            _file.Dispose();
            throw;
        }
    }

    public VaultFileFormat Format => _header.Format;
    public int ChunkSize => _header.ChunkSize;

    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => false;

    public override long Length
    {
        get
        {
            ThrowIfDisposed();
            return _header.PlainLength;
        }
    }

    public override long Position
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return _position;
            }
        }
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0 || count < 0 || offset > buffer.Length - count)
            throw new ArgumentOutOfRangeException(nameof(offset));

        lock (_gate)
        {
            ThrowIfDisposed();
            if (count == 0 || _position >= _header.PlainLength) return 0;

            int total = 0;
            while (count > 0 && _position < _header.PlainLength)
            {
                long chunkIndexLong = _position / _header.ChunkSize;
                if (chunkIndexLong >= _header.ChunkCount) break;
                int chunkIndex = (int)chunkIndexLong;
                int offsetInChunk = (int)(_position - chunkIndexLong * _header.ChunkSize);

                byte[] chunk = GetChunk(chunkIndex);
                int available = chunk.Length - offsetInChunk;
                if (available <= 0) break;

                int toCopy = Math.Min(available, count);
                Buffer.BlockCopy(chunk, offsetInChunk, buffer, offset, toCopy);
                _position += toCopy;
                offset += toCopy;
                count -= toCopy;
                total += toCopy;
            }

            return total;
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            long target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => _header.PlainLength + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };

            if (target < 0) throw new IOException("尝试定位到文件开头之前。");
            _position = target;
            return _position;
        }
    }

    public override void Flush()
    {
        // read-only stream
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private byte[] GetChunk(int index)
    {
        if (_cache.TryGetValue(index, out byte[]? cached)) return cached;

        int plainLength = GetChunkPlainLength(index);
        if (plainLength <= 0) throw new InvalidDataException("保险箱文件块长度无效。");

        _cipherScratch ??= new byte[_header.ChunkSize];
        var plain = new byte[plainLength];
        CryptoService.DecryptChunkInto(_file, _aes, _header, index, plain, _cipherScratch);

        _cache[index] = plain;
        _cacheOrder.Enqueue(index);
        while (_cacheOrder.Count > MaxCachedChunks)
        {
            int evicted = _cacheOrder.Dequeue();
            if (_cache.Remove(evicted, out byte[]? old))
            {
                CryptographicOperations.ZeroMemory(old);
            }
        }

        return plain;
    }

    private int GetChunkPlainLength(int index)
    {
        if ((uint)index >= (uint)_header.ChunkCount) throw new ArgumentOutOfRangeException(nameof(index));
        if (_header.ChunkTable != null) return _header.ChunkTable[index].Length;
        long plainBefore = (long)index * _header.ChunkSize;
        return (int)Math.Min(_header.ChunkSize, _header.PlainLength - plainBefore);
    }

    protected override void Dispose(bool disposing)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;

            if (disposing)
            {
                foreach (byte[] chunk in _cache.Values)
                {
                    CryptographicOperations.ZeroMemory(chunk);
                }
                _cache.Clear();

                if (_cipherScratch != null)
                {
                    CryptographicOperations.ZeroMemory(_cipherScratch);
                    _cipherScratch = null;
                }

                CryptographicOperations.ZeroMemory(_fileKey);
                _aes.Dispose();
                _file.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(VaultMediaStream));
    }
}
