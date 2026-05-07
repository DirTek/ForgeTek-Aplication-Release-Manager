# ForgeTek Update Client Integration Guide

This guide explains how to integrate ForgeTek Update Packager-generated updates into your application. Use this documentation to implement automatic update checking, download, and installation for your app.

## Overview

ForgeTek Update Packager creates two core artifacts for each app release:
1. **Update Catalog JSON**: Publicly accessible JSON file listing all available versions and download URLs
2. **.ftu Update Package**: Binary container with your app files, integrity checks, and metadata

Your application will poll the update catalog, download new packages, verify them, and apply updates.

---

## Update Catalog Format

The update catalog is a JSON file hosted at a public URL. The standard path pattern is:
`{baseUrl}/{appKey}/{appKey}.json` (e.g., `https://example.com/releases/stlorganizer/stlorganizer.json`)

Note: The extension of the catalog file can be customized. While `.ftu` is the default, packager supports custom extensions (e.g., `.stlo`). Update your catalog URL parsing to match.
Note 2: STLOrganizer is used as an example app name/key in this documentation. It will be replaced with your actual app name/key in your implementation.

### Catalog Structure
```json
{
  "stlorganizer": "0.1.2",
  "url": {
    "stlorganizer": "https://example.com/releases/stlorganizer/0.1.2/STLOrganizer-0.1.2.stlo"
  },
  "versions": {
    "0.1.1": {
      "url": "https://example.com/releases/stlorganizer/0.1.1/STLOrganizer-0.1.1.stlo",
      "date": "2026-04-30",
      "type": "incremental",
      "checksum": "0412bca9d0d375ad5d95a9429326cd997c5df9eff4f30fe10b1f3fc214a9baf9"
    },
    "0.1.2": {
      "url": "https://example.com/releases/stlorganizer/0.1.2/STLOrganizer-0.1.2.stlo",
      "date": "2026-04-30",
      "type": "incremental",
      "checksum": "64babf2a746b9dbc234fa0bbdbd1dc3b25e1914fdbe1f05f626d8fcf499a80af"
    }
  }
}
```

### Field Descriptions
| Field | Type | Description |
|-------|------|-------------|
| `{appKey}` | string | Current latest version number for your app |
| `url.{appKey}` | string | Download URL for the latest package |
| `versions.{version}` | object | Per-version metadata |
| `versions.{version}.url` | string | Download URL for this specific version |
| `versions.{version}.date` | string | Release date (YYYY-MM-DD) |
| `versions.{version}.type` | string | `incremental` (changed files only) or `full` (all files) |
| `versions.{version}.checksum` | string | SHA-256 checksum of the entire .ftu package |

---

## .ftu Package Format

.ftu files are binary containers with the following layout (all byte offsets are 0-indexed):

```
┌──────────────────────────────────────────────────────┐
│  [0..3]              Magic bytes: "FTUP" (4 ASCII bytes)  │
│  [4..7]              Header length (4 bytes, little-endian uint32) │
│  [8..8+hlen-1]       JSON header (UTF-8 encoded)              │
│  [8+hlen..n-33]      ZIP payload (standard ZIP archive)   │
│  [n-32..n-1]         SHA-256 checksum (last 32 bytes)      │
└──────────────────────────────────────────────────────┘
```

### JSON Header Structure
The header is embedded in the package and contains metadata:
```json
{
  "version": "0.1.2",
  "app": "STLOrganizer",
  "createdAt": "2026-04-30T11:27:00.7854434+00:00",
  "files": [
    {
      "path": "STLOrganizer.dll",
      "hash": "sha256-90ec0ff137c65d222094cf20539f338e7553732263c1aa06b4350ead457a21bd",
      "size": 2021744
    },
    {
      "path": "STLOrganizer.exe",
      "hash": "sha256-92b573252aa76a50d8a687d14010197dc455ce6066a53e28bf41026ce69cd62f",
      "size": 294768
    },
    {
      "path": "STLOrganizer.pdb",
      "hash": "sha256-9a9ccc8d758dee10dba437996faa1ac8975b03c0ea6a267ed7053b59715a8868",
      "size": 921588
    },
    {
      "path": "STLOrganizer.staticwebassets.endpoints.json",
      "hash": "sha256-02bdae549ea227de42bf50e7d894753d9a7a8f78c98c751b3dafb47749eb9c05",
      "size": 24903
    }
  ],
  "removedFiles": ["deprecated/config.json", "oldplugin.dll"],
  "totals": {
    "fileCount": 4,
    "totalSize": 3263003
  }
}
```

### Header Fields
| Field | Type | Description |
|-------|------|-------------|
| `App` | string | Your application's name/app key |
| `Version` | string | Package version number |
| `PackageType` | string | `incremental` or `full` |
| `CreatedAt` | string | ISO 8601 timestamp of package creation |
| `FileCount` | int | Number of files in the ZIP payload |
| `Files` | array | List of files with their relative paths and SHA-256 checksums |
| `RemovedFiles` | array | Relative paths of files deleted in this version — client should delete these after extraction (incremental packages only; empty for full packages) |

---

## Manifest Format (manifest.json)

Each package includes a `manifest.json` (either embedded in the ZIP or hosted alongside the package) with file-level details:
```json
{
  "version": "0.1.2",
  "app": "STLOrganizer",
  "createdAt": "2026-04-30T11:27:00.7854434+00:00",
  "files": [
    {
      "path": "STLOrganizer.dll",
      "hash": "sha256-90ec0ff137c65d222094cf20539f338e7553732263c1aa06b4350ead457a21bd",
      "size": 2021744
    },
    {
      "path": "STLOrganizer.exe",
      "hash": "sha256-92b573252aa76a50d8a687d14010197dc455ce6066a53e28bf41026ce69cd62f",
      "size": 294768
    },
    {
      "path": "STLOrganizer.pdb",
      "hash": "sha256-9a9ccc8d758dee10dba437996faa1ac8975b03c0ea6a267ed7053b59715a8868",
      "size": 921588
    },
    {
      "path": "STLOrganizer.staticwebassets.endpoints.json",
      "hash": "sha256-02bdae549ea227de42bf50e7d894753d9a7a8f78c98c751b3dafb47749eb9c05",
      "size": 24903
    }
  ],
  "removedFiles": ["deprecated/config.json", "oldplugin.dll"],
  "totals": {
    "fileCount": 4,
    "totalSize": 3263003
  }
}
```

### Manifest Fields
| Field | Type | Description |
|-------|------|-------------|
| `version` | string | Version number this manifest was generated for |
| `app` | string | Application name |
| `createdAt` | string | ISO 8601 timestamp of manifest creation |
| `files` | array | Files included in this update with path, SHA-256 hash, and size |
| `removedFiles` | array | Relative paths of files deleted in this version — client should delete these after extraction |
| `totals.fileCount` | int | Number of files in the `files` array |
| `totals.totalSize` | long | Sum of all file sizes in bytes |

---

## Step-by-Step Implementation

### 1. Check for Updates
Download and parse the update catalog, then compare the latest version to your app's current version.

```csharp
public async Task<string?> CheckForUpdateAsync(string catalogUrl, string currentVersion, string appKey)
{
    string catalogJson = await new HttpClient().GetStringAsync(catalogUrl);
    JsonNode catalog = JsonNode.Parse(catalogJson);
    string latestVersion = catalog[appKey]!.ToString();
    return Version.Parse(latestVersion) > Version.Parse(currentVersion) ? latestVersion : null;
}
```

### 2. Download Update Package
If a newer version is available, download the .ftu package from the catalog's URL.

```csharp
public async Task<string> DownloadPackageAsync(string catalogUrl, string targetVersion, string appKey, string downloadDir)
{
    string catalogJson = await new HttpClient().GetStringAsync(catalogUrl);
    JsonNode catalog = JsonNode.Parse(catalogJson);
    string packageUrl = catalog["versions"]![targetVersion]!["url"]!.ToString();
    byte[] packageBytes = await new HttpClient().GetByteArrayAsync(packageUrl);
    string packagePath = Path.Combine(downloadDir, $"{appKey}-{targetVersion}.ftu");
    await File.WriteAllBytesAsync(packagePath, packageBytes);
    return packagePath;
}
```

### 3. Verify Package Integrity
Validate the package's magic bytes, header, and SHA-256 checksum.

```csharp
public async Task<PackageHeader> VerifyPackageAsync(string packagePath)
{
    using FileStream fs = new(packagePath, FileMode.Open, FileAccess.Read);
    byte[] magic = new byte[4];
    await fs.ReadExactlyAsync(magic);
    if (!magic.SequenceEqual("FTUP"u8.ToArray()))
        throw new InvalidDataException("Invalid .ftu package: Missing FTUP magic bytes");
    byte[] lenBuf = new byte[4];
    await fs.ReadExactlyAsync(lenBuf);
    uint headerLen = BitConverter.ToUInt32(lenBuf);
    byte[] headerBuf = new byte[headerLen];
    await fs.ReadExactlyAsync(headerBuf);
    string headerJson = Encoding.UTF8.GetString(headerBuf);
    PackageHeader? header = JsonSerializer.Deserialize<PackageHeader>(headerJson);
    fs.Seek(0, SeekOrigin.Begin);
    byte[] packageBytes = new byte[fs.Length];
    await fs.ReadExactlyAsync(packageBytes);
    byte[] storedChecksum = packageBytes[^32..];
    byte[] computedChecksum = SHA256.HashData(packageBytes[..^32]);
    if (!storedChecksum.SequenceEqual(computedChecksum))
        throw new InvalidDataException("Package checksum mismatch: Possible tampering detected");
    return header!;
}

public class PackageHeader
{
    public string App { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string PackageType { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public int FileCount { get; set; }
    public List<PackageHeaderFile> Files { get; set; } = [];
    public List<string> RemovedFiles { get; set; } = [];
}

public class PackageHeaderFile
{
    public string Path { get; set; } = string.Empty;
    public string Checksum { get; set; } = string.Empty;
}
```

### 4. Extract Package Contents
Read the ZIP payload from the verified package and extract files.

```csharp
public async Task ExtractPackageAsync(string packagePath, string installDir)
{
    using FileStream fs = new(packagePath, FileMode.Open, FileAccess.Read);
    byte[] lenBuf = new byte[4];
    await fs.ReadExactlyAsync(new byte[4]);
    await fs.ReadExactlyAsync(lenBuf);
    uint headerLen = BitConverter.ToUInt32(lenBuf);
    fs.Seek(headerLen, SeekOrigin.Current);
    long zipStart = fs.Position;
    long zipEnd = fs.Length - 32;
    byte[] zipData = new byte[zipEnd - zipStart];
    await fs.ReadExactlyAsync(zipData);
    using MemoryStream zipStream = new(zipData);
    using ZipArchive archive = new(zipStream, ZipArchiveMode.Read);
    foreach (ZipArchiveEntry entry in archive.Entries)
    {
        string targetPath = Path.Combine(installDir, entry.FullName);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        await using FileStream entryStream = entry.Open();
        await using FileStream targetStream = new(targetPath, FileMode.Create);
        await entryStream.CopyToAsync(targetStream);
    }
}
```

### 5. Apply Update
Handle incremental vs full packages appropriately:

- **Incremental packages**: Overwrite existing files in your installation directory. The package only contains added/modified files. After extraction, delete any files listed in `header.RemovedFiles` to prevent stale file bloat.
- **Full packages**: Can safely replace all files in your installation directory (contains all non-debug application files). `RemovedFiles` will be empty for full packages.

```csharp
public async Task ApplyUpdate(string packagePath, string installDir, string currentVersion)
{
    PackageHeader header = await VerifyPackageAsync(packagePath);
    await ExtractPackageAsync(packagePath, installDir);

    // Delete files removed in this version (incremental packages only)
    if (header.RemovedFiles != null)
    {
        foreach (var relativePath in header.RemovedFiles)
        {
            var fullPath = Path.Combine(installDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(fullPath))
                File.Delete(fullPath);
        }
    }

    if (header.PackageType == "incremental")
        Console.WriteLine($"Applied incremental update {header.Version}");
    else
        Console.WriteLine($"Applied full update {header.Version}");
}
```

---

## Error Handling

| Error | Cause | Resolution |
|-------|-------|------------|
| `InvalidDataException: Missing FTUP magic bytes` | Corrupt or non-.ftu file | Re-download the package |
| `InvalidDataException: Package checksum mismatch` | Tampered or corrupt package | Re-download the package |
| `JsonException` | Malformed package header | Re-download the package |
| `FileNotFoundException` | Missing file in ZIP payload | Re-download the package |

---

## Configuration Notes

- **Package Extensions**: While `.ftu` is the default, packager supports custom extensions (e.g., `.stlo`). Update your catalog URL parsing to match.
- **Hosting**: Packages and catalogs can be hosted on any HTTP/HTTPS server, FTP (with direct links), or cloud storage with public access.
- **Security**: The SHA-256 footer ensures package integrity. For additional security, use HTTPS for all downloads and consider code signing your application executables.

---

## Support

For issues with the update packager or this documentation, report to: dirtek@gmail.com
