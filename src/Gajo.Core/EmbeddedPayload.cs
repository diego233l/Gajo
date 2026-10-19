using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Gajo.Core
{
    /// <summary>
    /// Stores ffmpeg.exe inside Gajo.exe: the build appends a Brotli-compressed copy after the
    /// single-file bundle, followed by a 64-byte trailer. At runtime it is unpacked once into
    /// %TEMP%\Gajo and reused. Appended bytes are ignored by the .NET single-file host, which
    /// locates its bundle through an offset stored in the header, not from the end of the file.
    ///
    /// Trailer layout (64 bytes, at the very end of the exe):
    ///   [0..16)  magic "GAJO-PAYLOAD-v1\0"
    ///   [16..48) SHA-256 of the uncompressed file
    ///   [48..56) compressed length   (Int64, little endian)
    ///   [56..64) uncompressed length (Int64, little endian)
    /// </summary>
    public static class EmbeddedPayload
    {
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("GAJO-PAYLOAD-v1\0");
        private const int TrailerSize = 64;

        public sealed record Info(long PayloadOffset, long CompressedLength, long Length, string Sha256);

        /// <summary>Build step: append <paramref name="payloadPath"/> to <paramref name="hostExe"/>.</summary>
        public static Info Append(string hostExe, string payloadPath, int quality = 9)
        {
            if (Read(hostExe) != null) throw new InvalidOperationException("El ejecutable ya contiene un motor integrado.");

            byte[] hash;
            long length = new FileInfo(payloadPath).Length;
            using (var src = File.OpenRead(payloadPath)) hash = SHA256.HashData(src);

            using var host = new FileStream(hostExe, FileMode.Append, FileAccess.Write);
            long start = host.Position;
            using (var src = File.OpenRead(payloadPath))
            using (var brotli = new BrotliEncoderStream(host, quality))
                src.CopyTo(brotli, 1 << 20);
            long compressed = host.Position - start;

            var trailer = new byte[TrailerSize];
            Magic.CopyTo(trailer, 0);
            hash.CopyTo(trailer, 16);
            BitConverter.GetBytes(compressed).CopyTo(trailer, 48);
            BitConverter.GetBytes(length).CopyTo(trailer, 56);
            host.Write(trailer, 0, trailer.Length);
            return new Info(start, compressed, length, Convert.ToHexString(hash));
        }

        public static Info? Read(string exePath)
        {
            try
            {
                using var fs = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (fs.Length < TrailerSize) return null;
                var t = new byte[TrailerSize];
                fs.Seek(-TrailerSize, SeekOrigin.End);
                fs.ReadExactly(t);
                if (!t.AsSpan(0, 16).SequenceEqual(Magic)) return null;
                long compressed = BitConverter.ToInt64(t, 48);
                long length = BitConverter.ToInt64(t, 56);
                long offset = fs.Length - TrailerSize - compressed;
                if (compressed <= 0 || offset < 0) return null;
                return new Info(offset, compressed, length, Convert.ToHexString(t, 16, 32));
            }
            catch { return null; }
        }

        /// <summary>
        /// Unpacks the payload to <paramref name="folder"/>/<paramref name="namePrefix"/>-HASH<paramref name="extension"/>
        /// (only if not already there) and removes copies left by older versions of Gajo.
        /// </summary>
        public static string? Extract(string exePath, string folder, string namePrefix, string extension)
        {
            var info = Read(exePath);
            if (info == null) return null;

            Directory.CreateDirectory(folder);
            var target = Path.Combine(folder, $"{namePrefix}-{info.Sha256[..12].ToLowerInvariant()}{extension}");

            if (!(File.Exists(target) && new FileInfo(target).Length == info.Length))
            {
                var temp = target + "." + Guid.NewGuid().ToString("N")[..8] + ".part";
                try
                {
                    using (var fs = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        fs.Seek(info.PayloadOffset, SeekOrigin.Begin);
                        using var slice = new SubStream(fs, info.CompressedLength);
                        using var brotli = new BrotliStream(slice, CompressionMode.Decompress);
                        using var dst = File.Create(temp);
                        brotli.CopyTo(dst, 1 << 20);
                    }
                    if (new FileInfo(temp).Length != info.Length) throw new InvalidDataException("Motor integrado dañado.");
                    try { File.Move(temp, target, overwrite: true); }
                    catch (IOException) when (File.Exists(target) && new FileInfo(target).Length == info.Length)
                    {
                        // Another Gajo process won the race and the file is in use: fine.
                    }
                }
                finally
                {
                    try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                }
            }

            // Clean up copies from previous builds (and stray partial files).
            foreach (var old in Directory.EnumerateFiles(folder, namePrefix + "-*"))
            {
                if (string.Equals(old, target, StringComparison.OrdinalIgnoreCase)) continue;
                try { File.Delete(old); } catch { /* in use by an older running copy */ }
            }
            return target;
        }

        private sealed class BrotliEncoderStream : Stream
        {
            private readonly Stream _inner;
            private BrotliEncoder _encoder;
            private readonly byte[] _out = new byte[1 << 20];

            public BrotliEncoderStream(Stream inner, int quality)
            {
                _inner = inner;
                _encoder = new BrotliEncoder(quality, 24);
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                var src = new ReadOnlySpan<byte>(buffer, offset, count);
                while (true)
                {
                    var status = _encoder.Compress(src, _out, out int consumed, out int written, isFinalBlock: false);
                    _inner.Write(_out, 0, written);
                    src = src[consumed..];
                    if (status == System.Buffers.OperationStatus.InvalidData) throw new InvalidDataException();
                    if (src.IsEmpty && status != System.Buffers.OperationStatus.DestinationTooSmall) break;
                }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    while (true)
                    {
                        var status = _encoder.Compress(ReadOnlySpan<byte>.Empty, _out, out _, out int written, isFinalBlock: true);
                        _inner.Write(_out, 0, written);
                        if (status == System.Buffers.OperationStatus.Done) break;
                    }
                    _encoder.Dispose();
                }
                base.Dispose(disposing);
            }

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() => _inner.Flush();
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }

        private sealed class SubStream : Stream
        {
            private readonly Stream _inner;
            private long _remaining;
            public SubStream(Stream inner, long length) { _inner = inner; _remaining = length; }
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_remaining <= 0) return 0;
                int n = _inner.Read(buffer, offset, (int)Math.Min(count, _remaining));
                _remaining -= n;
                return n;
            }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
