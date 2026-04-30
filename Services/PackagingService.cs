using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ForgeTekUpdatePackager.Models;
using JsonException = System.Text.Json.JsonException;

namespace ForgeTekUpdatePackager.Services;

/// <summary>
/// Builds the .ftu (or custom-extension) package container.
///
/// Binary layout
/// ─────────────
///   [0..3]              Magic bytes  "FTUP"  (4 bytes, ASCII)
///   [4..7]              Header length        (4 bytes, little-endian uint32)
///   [8..8+hlen-1]       JSON header          (UTF-8, no BOM)
///   [8+hlen..]          ZIP payload          (standard Deflate/Stored ZIP)
///   [last 32 bytes]     SHA-256              of every byte that precedes it
///
/// The JSON header is a <see cref="PackageHeader"/> object.
/// </summary>
public class PackagingService
{
    private static readonly byte[] Magic = "FTUP"u8.ToArray();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // ── Public entry point ───────────────────────────────────────────────────

    /// <summary>
    /// Writes the package file to <paramref name="outputPath"/> and returns the
    /// hex SHA-256 of the finished file.
    /// </summary>
    /// <param name="manifestPath">
    /// Path to the already-generated manifest.json on disk. When provided it is
    /// embedded as the first entry in the ZIP payload so receivers can verify
    /// per-file checksums after extraction.
    /// </param>
    public async Task<string> BuildAsync(
        AppEntry entry,
        AppVersion version,
        IReadOnlyList<FileRecord> files,
        PackageType packageType,
        string outputPath,
        string? manifestPath,
        IProgress<string> progress,
        CancellationToken ct = default)
    {
        progress.Report($"Building {packageType.ToString().ToLower()} package for v{version.VersionNumber}…");

        var hasManifest = !string.IsNullOrWhiteSpace(manifestPath) && File.Exists(manifestPath);
        var totalFiles  = files.Count + (hasManifest ? 1 : 0);
        progress.Report($"  {files.Count} release file(s){(hasManifest ? " + manifest.json" : string.Empty)}.");

        // 1. Build the in-memory ZIP
        progress.Report(string.Empty);
        progress.Report("Compressing files…");

        using var zipStream = new MemoryStream();
        await BuildZipAsync(entry, files, hasManifest ? manifestPath : null, zipStream, progress, ct);
        var zipBytes = zipStream.ToArray();

        progress.Report($"  ZIP payload: {FormatSize(zipBytes.Length)}");

        // 2. Build the JSON header
        var header = new PackageHeader
        {
            App         = entry.Name,
            Version     = version.VersionNumber,
            PackageType = packageType.ToString().ToLower(),
            CreatedAt   = DateTimeOffset.UtcNow,
            FileCount   = totalFiles,
            Files       = files.Select(f => new PackageHeaderFile
            {
                Path     = f.Path.Replace('\\', '/'),
                Checksum = f.Checksum,
            }).ToList(),
        };

        var headerJson  = JsonSerializer.Serialize(header, JsonOptions);
        var headerBytes = Encoding.UTF8.GetBytes(headerJson);

        if (headerBytes.Length > int.MaxValue)
            throw new InvalidOperationException("Header is too large.");

        // 3. Assemble the container
        progress.Report(string.Empty);
        progress.Report("Writing container…");

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        // Write to a temp file so we can compute a streaming SHA-256 as we go,
        // then append the hash at the end.
        var tmpPath = outputPath + ".tmp";
        string hashHex;
        try
        {
            using (var sha   = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            using (var fsTmp = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None,
                                              bufferSize: 65536, useAsync: true))
            {
                // Magic
                await fsTmp.WriteAsync(Magic, ct);
                sha.AppendData(Magic);

                // Header length (little-endian uint32)
                var lenBytes = BitConverter.GetBytes((uint)headerBytes.Length);
                if (!BitConverter.IsLittleEndian) Array.Reverse(lenBytes);
                await fsTmp.WriteAsync(lenBytes, ct);
                sha.AppendData(lenBytes);

                // JSON header
                await fsTmp.WriteAsync(headerBytes, ct);
                sha.AppendData(headerBytes);

                // ZIP payload
                await fsTmp.WriteAsync(zipBytes, ct);
                sha.AppendData(zipBytes);

                // SHA-256 footer
                var hash = sha.GetCurrentHash();
                await fsTmp.WriteAsync(hash, ct);
                await fsTmp.FlushAsync(ct);
                // fsTmp is disposed here — file is fully closed before Move

                hashHex = Convert.ToHexString(hash).ToLowerInvariant();
            }

            progress.Report($"  SHA-256: {hashHex}");

            // File is now closed — safe to move
            if (File.Exists(outputPath)) File.Delete(outputPath);
            File.Move(tmpPath, outputPath);

            progress.Report(string.Empty);
            progress.Report($"✔  Package saved → {outputPath}");
            progress.Report($"   Size: {FormatSize(new FileInfo(outputPath).Length)}");

            return hashHex;
        }
        catch
        {
            if (File.Exists(tmpPath)) File.Delete(tmpPath);
            throw;
        }
    }

    // ── Verification ─────────────────────────────────────────────────────────

    /// <summary>
    /// Re-reads the package at <paramref name="filePath"/> and verifies:
    ///   1. Magic bytes are "FTUP"
    ///   2. Header JSON is parseable
    ///   3. SHA-256 footer matches a fresh streaming computation over the file
    ///   4. Embedded ZIP opens and contains every file declared in the header
    /// Throws <see cref="InvalidDataException"/> on any failure.
    /// Uses streaming I/O — no full-file allocation, safe for large packages.
    /// </summary>
    public async Task VerifyAsync(string filePath, IProgress<string> progress, CancellationToken ct = default)
    {
        progress.Report("Verifying package integrity…");

        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                                            FileShare.Read, 65536, useAsync: true);
        var fileSize = fs.Length;

        if (fileSize < 4 + 4 + 32)
            throw new InvalidDataException("File is too small to be a valid FTUP package.");

        // ── 1. Magic bytes ──────────────────────────────────────────────────
        var magic = new byte[4];
        await fs.ReadExactlyAsync(magic, ct);
        if (!magic.AsSpan().SequenceEqual(Magic))
            throw new InvalidDataException("Magic bytes invalid — not an FTUP package.");
        progress.Report("  ✔  Magic: FTUP");

        // ── 2. Header length + JSON ─────────────────────────────────────────
        var lenBuf = new byte[4];
        await fs.ReadExactlyAsync(lenBuf, ct);
        if (!BitConverter.IsLittleEndian) Array.Reverse(lenBuf);
        var headerLen     = BitConverter.ToUInt32(lenBuf, 0);
        long payloadOffset  = 8 + headerLen;
        long checksumOffset = fileSize - 32;

        if (payloadOffset > checksumOffset)
            throw new InvalidDataException($"Header length ({headerLen:N0} B) overflows the file.");

        var headerBuf = new byte[headerLen];
        await fs.ReadExactlyAsync(headerBuf, ct);

        PackageHeader header;
        try
        {
            header = JsonSerializer.Deserialize<PackageHeader>(
                         Encoding.UTF8.GetString(headerBuf), JsonOptions)
                     ?? throw new InvalidDataException("Header JSON deserialized to null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Header JSON is malformed: {ex.Message}");
        }

        progress.Report($"  ✔  Header: {header.App} v{header.Version} ({header.PackageType}, {header.FileCount} file(s))");

        // ── 3. SHA-256 (streamed — no full-file allocation) ─────────────────
        fs.Seek(0, SeekOrigin.Begin);
        using var sha  = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var       buf  = new byte[65536];
        var       left = checksumOffset;

        while (left > 0)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = (int)Math.Min(left, buf.Length);
            var read  = await fs.ReadAsync(buf.AsMemory(0, chunk), ct);
            if (read == 0) break;
            sha.AppendData(buf.AsSpan(0, read));
            left -= read;
        }

        var computed = sha.GetCurrentHash();
        var stored   = new byte[32];
        fs.Seek(-32, SeekOrigin.End);
        await fs.ReadExactlyAsync(stored, ct);

        if (!computed.AsSpan().SequenceEqual(stored))
            throw new InvalidDataException(
                $"SHA-256 mismatch — stored: {Convert.ToHexString(stored).ToLowerInvariant()}, " +
                $"computed: {Convert.ToHexString(computed).ToLowerInvariant()}");

        progress.Report($"  ✔  SHA-256: {Convert.ToHexString(computed).ToLowerInvariant()}");

        // ── 4. ZIP structure (via slice — no full-ZIP allocation) ────────────
        var zipLen = checksumOffset - payloadOffset;
        using var slice = new SliceStream(fs, payloadOffset, zipLen);

        ZipArchive archive;
        try   { archive = new ZipArchive(slice, ZipArchiveMode.Read, leaveOpen: true); }
        catch (Exception ex) { throw new InvalidDataException($"ZIP payload could not be opened: {ex.Message}"); }

        int         zipCount;
        List<string> missing;
        using (archive)
        {
            zipCount = archive.Entries.Count;
            var entrySet = archive.Entries
                .Select(e => e.FullName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            missing = header.Files
                .Select(f => f.Path.Replace('\\', '/'))
                .Where(p => !entrySet.Contains(p))
                .ToList();
        }

        if (zipCount != header.FileCount)
            throw new InvalidDataException($"ZIP entry count ({zipCount}) ≠ header FileCount ({header.FileCount}).");

        if (missing.Count > 0)
            throw new InvalidDataException(
                $"Missing ZIP entries: {string.Join(", ", missing.Take(5))}" +
                (missing.Count > 5 ? $" … (+{missing.Count - 5} more)" : string.Empty));

        progress.Report($"  ✔  ZIP: {zipCount} entries, all declared files present");
        progress.Report("✔  Package verified.");
    }

    // Presents a seekable, read-only window over an existing stream so ZipArchive
    // can parse the embedded ZIP without copying the entire payload into memory.
    private sealed class SliceStream(Stream inner, long offset, long length) : Stream
    {
        private long _pos;

        public override bool CanRead  => true;
        public override bool CanSeek  => true;
        public override bool CanWrite => false;
        public override long Length   => length;

        public override long Position
        {
            get => _pos;
            set { _pos = value; inner.Seek(offset + value, SeekOrigin.Begin); }
        }

        public override int Read(byte[] buffer, int bufOffset, int count)
        {
            inner.Seek(offset + _pos, SeekOrigin.Begin);
            var maxRead = (int)Math.Min(count, length - _pos);
            if (maxRead <= 0) return 0;
            var read = inner.Read(buffer, bufOffset, maxRead);
            _pos += read;
            return read;
        }

        public override long Seek(long off, SeekOrigin origin)
        {
            _pos = origin switch
            {
                SeekOrigin.Begin   => off,
                SeekOrigin.Current => _pos + off,
                SeekOrigin.End     => length + off,
                _                  => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            return _pos;
        }

        public override void Flush() { }
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    // ── ZIP building ─────────────────────────────────────────────────────────

    private static async Task BuildZipAsync(
        AppEntry entry,
        IReadOnlyList<FileRecord> files,
        string? manifestPath,
        Stream destination,
        IProgress<string> progress,
        CancellationToken ct)
    {
        using var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);

        // Embed manifest.json first so receivers can locate it at a fixed position
        if (manifestPath is not null)
        {
            ct.ThrowIfCancellationRequested();
            progress.Report("  [manifest] manifest.json");

            var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
            manifestEntry.LastWriteTime = DateTimeOffset.UtcNow;

            await using var src  = new FileStream(manifestPath, FileMode.Open, FileAccess.Read,
                                                  FileShare.Read, 65536, true);
            await using var dest = manifestEntry.Open();
            await src.CopyToAsync(dest, ct);
        }

        for (var i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var record    = files[i];
            var fullPath  = Path.Combine(entry.FolderPath, record.Path);
            var entryName = record.Path.Replace('\\', '/');

            progress.Report($"  [{i + 1}/{files.Count}] {entryName}");

            if (!File.Exists(fullPath))
            {
                progress.Report("    ⚠ File not found on disk — skipped");
                continue;
            }

            var zipEntry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            // DateModified is stored as a plain DateTime (unspecified kind). Treat it as UTC
            // to avoid the "offset mismatch" exception when constructing DateTimeOffset.
            zipEntry.LastWriteTime = record.DateModified != default
                ? new DateTimeOffset(DateTime.SpecifyKind(record.DateModified, DateTimeKind.Utc))
                : DateTimeOffset.UtcNow;

            await using var src  = new FileStream(fullPath, FileMode.Open, FileAccess.Read,
                                                  FileShare.Read, 65536, true);
            await using var dest = zipEntry.Open();
            await src.CopyToAsync(dest, ct);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024               => $"{bytes} B",
        < 1024 * 1024        => $"{bytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _                    => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
    };
}

// ── Header model (serialized into the container) ─────────────────────────────

internal sealed class PackageHeader
{
    public string App         { get; set; } = string.Empty;
    public string Version     { get; set; } = string.Empty;
    public string PackageType { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public int FileCount     { get; set; }
    public List<PackageHeaderFile> Files { get; set; } = [];
}

internal sealed class PackageHeaderFile
{
    public string Path     { get; set; } = string.Empty;
    public string Checksum { get; set; } = string.Empty;
}
