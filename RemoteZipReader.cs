using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace AsmoFrBoxTelegramBot;

/// <summary>
/// Reads a ZIP file over HTTP without downloading it. Fetches only the
/// end-of-central-directory record, the central directory itself, and then,
/// per requested entry, its local header plus exactly its compressed bytes.
/// Unchanged from the original app -- only the namespace moved.
/// </summary>
public static class RemoteZipReader
{
    public sealed class RemoteZipEntry
    {
        public required string Name { get; init; }
        public long CompressedSize { get; init; }
        public long UncompressedSize { get; init; }
        public long LocalHeaderOffset { get; init; }
        public ushort CompressionMethod { get; init; }
        public uint Crc32 { get; init; }
    }

    private const int EocdFixedSize = 22;
    private const int EocdMaxCommentSize = 65535;
    private const int Zip64LocatorSize = 20;
    private const uint EocdSignature = 0x06054b50;
    private const uint Zip64EocdLocatorSignature = 0x07064b50;
    private const uint Zip64EocdSignature = 0x06064b50;
    private const uint CentralDirSignature = 0x02014b50;
    private const uint LocalHeaderSignature = 0x04034b50;
    private const uint Zip64ExtraFieldId = 0x0001;

    public static async Task<List<RemoteZipEntry>> ListEntriesAsync(
        HttpClient http,
        string url,
        Action<HttpRequestMessage>? configureRequest,
        CancellationToken ct = default)
    {
        int tailSize = EocdFixedSize + EocdMaxCommentSize + Zip64LocatorSize;
        byte[] tail = await GetSuffixRangeAsync(http, url, tailSize, configureRequest, ct)
            .ConfigureAwait(false);

        int eocdPos = FindSignatureFromEnd(tail, EocdSignature);
        if (eocdPos < 0)
        {
            throw new InvalidOperationException(
                "Could not find the ZIP end-of-central-directory record at the tail of the file. " +
                "Either this isn't a valid ZIP, or the server doesn't honor Range requests.");
        }

        long cdOffset = ReadUInt32(tail, eocdPos + 16);
        long cdSize = ReadUInt32(tail, eocdPos + 12);
        long totalEntries = ReadUInt16(tail, eocdPos + 10);

        if (cdOffset == 0xFFFFFFFF || cdSize == 0xFFFFFFFF || totalEntries == 0xFFFF)
        {
            int locatorPos = eocdPos - Zip64LocatorSize;
            if (locatorPos < 0 || ReadUInt32(tail, locatorPos) != Zip64EocdLocatorSignature)
            {
                throw new InvalidOperationException(
                    "ZIP looks like it needs Zip64 (fields larger than 4 GB) but the Zip64 locator is missing.");
            }

            long zip64EocdOffset = ReadInt64(tail, locatorPos + 8);
            byte[] zip64Rec = await GetRangeAsync(http, url, zip64EocdOffset, 56, configureRequest, ct)
                .ConfigureAwait(false);
            if (ReadUInt32(zip64Rec, 0) != Zip64EocdSignature)
                throw new InvalidOperationException("Zip64 end-of-central-directory record looks corrupt.");

            totalEntries = ReadInt64(zip64Rec, 32);
            cdSize = ReadInt64(zip64Rec, 40);
            cdOffset = ReadInt64(zip64Rec, 48);
        }

        byte[] cd = await GetRangeAsync(http, url, cdOffset, cdSize, configureRequest, ct)
            .ConfigureAwait(false);

        var results = new List<RemoteZipEntry>((int)Math.Min(totalEntries, int.MaxValue));
        int pos = 0;
        while (pos + 46 <= cd.Length && ReadUInt32(cd, pos) == CentralDirSignature)
        {
            ushort compMethod = ReadUInt16(cd, pos + 10);
            uint crc32 = ReadUInt32(cd, pos + 16);
            long compSize = ReadUInt32(cd, pos + 20);
            long uncompSize = ReadUInt32(cd, pos + 24);
            int nameLen = ReadUInt16(cd, pos + 28);
            int extraLen = ReadUInt16(cd, pos + 30);
            int commentLen = ReadUInt16(cd, pos + 32);
            long localHeaderOffset = ReadUInt32(cd, pos + 42);

            string name = Encoding.UTF8.GetString(cd, pos + 46, nameLen);

            if (compSize == 0xFFFFFFFF || uncompSize == 0xFFFFFFFF || localHeaderOffset == 0xFFFFFFFF)
            {
                ApplyZip64Extra(cd, pos + 46 + nameLen, extraLen,
                    ref uncompSize, ref compSize, ref localHeaderOffset);
            }

            results.Add(new RemoteZipEntry
            {
                Name = name,
                CompressedSize = compSize,
                UncompressedSize = uncompSize,
                LocalHeaderOffset = localHeaderOffset,
                CompressionMethod = compMethod,
                Crc32 = crc32,
            });

            pos += 46 + nameLen + extraLen + commentLen;
        }

        return results;
    }

    public static async Task ExtractEntryAsync(
        HttpClient http,
        string url,
        RemoteZipEntry entry,
        string destPath,
        Action<HttpRequestMessage>? configureRequest,
        IProgress<(long done, long total)>? progress,
        CancellationToken ct = default)
    {
        const int initialHeaderGuess = 256;
        byte[] header = await GetRangeAsync(http, url, entry.LocalHeaderOffset, initialHeaderGuess, configureRequest, ct)
            .ConfigureAwait(false);
        if (ReadUInt32(header, 0) != LocalHeaderSignature)
            throw new InvalidOperationException($"Local file header for '{entry.Name}' looks corrupt.");

        int nameLen = ReadUInt16(header, 26);
        int extraLen = ReadUInt16(header, 28);
        int headerLen = 30 + nameLen + extraLen;
        if (headerLen > header.Length)
        {
            header = await GetRangeAsync(http, url, entry.LocalHeaderOffset, headerLen, configureRequest, ct)
                .ConfigureAwait(false);
        }

        long dataStart = entry.LocalHeaderOffset + headerLen;

        var dir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var crc = new Crc32();

        if (entry.CompressedSize == 0)
        {
            await using var empty = File.Create(destPath);
            return;
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Range = new RangeHeaderValue(dataStart, dataStart + entry.CompressedSize - 1);
        configureRequest?.Invoke(req);

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        EnsurePartialContent(resp);

        await using var input = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        Stream source = entry.CompressionMethod switch
        {
            0 => input,
            8 => new DeflateStream(input, CompressionMode.Decompress),
            _ => throw new NotSupportedException(
                $"Unsupported ZIP compression method ({entry.CompressionMethod}) for '{entry.Name}'. " +
                "Only stored (0) and deflate (8) are supported."),
        };

        try
        {
            await using (var output = new FileStream(
                destPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 128, useAsync: true))
            {
                var buffer = new byte[1024 * 256];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
                {
                    crc.Update(buffer.AsSpan(0, read));
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    done += read;
                    progress?.Report((done, entry.UncompressedSize));
                }
            }
        }
        finally
        {
            if (source is DeflateStream ds) ds.Dispose();
        }

        if (crc.Value != entry.Crc32)
        {
            try { File.Delete(destPath); } catch { /* best effort */ }
            throw new InvalidOperationException(
                $"CRC-32 mismatch extracting '{entry.Name}' -- expected {entry.Crc32:x8}, got {crc.Value:x8}. " +
                "The download may have been corrupted or truncated; try again.");
        }
    }

    private static void ApplyZip64Extra(
        byte[] cd, int extraStart, int extraLen,
        ref long uncompSize, ref long compSize, ref long localHeaderOffset)
    {
        int extraEnd = extraStart + extraLen;
        int p = extraStart;
        while (p + 4 <= extraEnd)
        {
            ushort id = ReadUInt16(cd, p);
            ushort size = ReadUInt16(cd, p + 2);
            int dataStart = p + 4;
            if (id == Zip64ExtraFieldId)
            {
                int c = 0;
                if (uncompSize == 0xFFFFFFFF && dataStart + c + 8 <= extraEnd)
                {
                    uncompSize = ReadInt64(cd, dataStart + c);
                    c += 8;
                }
                if (compSize == 0xFFFFFFFF && dataStart + c + 8 <= extraEnd)
                {
                    compSize = ReadInt64(cd, dataStart + c);
                    c += 8;
                }
                if (localHeaderOffset == 0xFFFFFFFF && dataStart + c + 8 <= extraEnd)
                {
                    localHeaderOffset = ReadInt64(cd, dataStart + c);
                }
                return;
            }
            p += 4 + size;
        }
    }

    private static void EnsurePartialContent(HttpResponseMessage resp)
    {
        if (resp.StatusCode != HttpStatusCode.PartialContent)
        {
            throw new InvalidOperationException(
                "Server ignored the Range request and returned the whole file -- it doesn't support " +
                "partial/ranged downloads, so entries can't be extracted without downloading the entire ZIP.");
        }
    }

    private static async Task<byte[]> GetRangeAsync(
        HttpClient http, string url, long start, long length,
        Action<HttpRequestMessage>? configureRequest, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Range = new RangeHeaderValue(start, start + length - 1);
        configureRequest?.Invoke(req);
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        EnsurePartialContent(resp);
        return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    private static async Task<byte[]> GetSuffixRangeAsync(
        HttpClient http, string url, int suffixLength,
        Action<HttpRequestMessage>? configureRequest, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Range = new RangeHeaderValue(null, suffixLength);
        configureRequest?.Invoke(req);
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        EnsurePartialContent(resp);
        return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    private static int FindSignatureFromEnd(byte[] buf, uint signature)
    {
        byte b0 = (byte)signature, b1 = (byte)(signature >> 8), b2 = (byte)(signature >> 16), b3 = (byte)(signature >> 24);
        for (int i = buf.Length - 4; i >= 0; i--)
        {
            if (buf[i] == b0 && buf[i + 1] == b1 && buf[i + 2] == b2 && buf[i + 3] == b3)
                return i;
        }
        return -1;
    }

    private static ushort ReadUInt16(byte[] buf, int offset) => BitConverter.ToUInt16(buf, offset);
    private static uint ReadUInt32(byte[] buf, int offset) => BitConverter.ToUInt32(buf, offset);
    private static long ReadInt64(byte[] buf, int offset) => BitConverter.ToInt64(buf, offset);

    private sealed class Crc32
    {
        private static readonly uint[] Table = BuildTable();
        private uint _value = 0xFFFFFFFF;

        public uint Value => _value ^ 0xFFFFFFFF;

        public void Update(ReadOnlySpan<byte> data)
        {
            uint c = _value;
            foreach (byte b in data)
                c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
            _value = c;
        }

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                table[i] = c;
            }
            return table;
        }
    }
}
