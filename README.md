# FARM — ForgeTek Application Release Manager

**FARM** is a desktop tool that covers the whole "ship my app" journey: getting the first install onto a user's machine, and keeping it up to date afterward. You point it at your app's build output, and it can both **build an installer** and **publish auto-updates** — without the moving parts that usually come with either.

What FARM does for you:

**Auto-updates**

- **Packages releases** into self-contained `.ftu` files with built-in SHA-256 integrity checks.
- **Builds incremental updates** automatically — it diffs against a baseline so users download only what changed, not the whole app every time.
- **Maintains the update catalog** (a simple JSON file) with stable/beta channels and full version history.
- **Publishes everywhere** — FTP, SFTP, S3, or GitHub Releases — and can roll a release back if something goes wrong.
- **Self-healing client logic** — the format is designed so a client can always recover a complete, correct install no matter what version it started from.

**Installer setups**

- **Builds a single-file installer `.exe`** that bundles one or more of your apps into a guided, branded setup wizard — no separate installer toolchain to learn.
- **Bundles redistributables** (e.g. .NET, VC++ runtimes) with detection rules so they install only when missing.
- **Runs custom pre/post-install actions** — scripts, executables, registry entries — plus EULA, shortcuts, and per-app optional/required selection.
- **Brands the experience** with your icon, banner/background, color theme, and button styling; can also emit a plain **portable ZIP** alongside the installer.
- **Signs and self-uninstalls** — optional Authenticode signing of the output, and a per-app uninstaller baked into each install.

**Signing, storage & team workflow**

- **Generates code-signing certificates** — create a self-signed code-signing PFX right in the app (or import your own), then sign your packages and installers with it.
- **Runs solo or networked** — store everything in a local **SQLite** file for single-machine use (the zero-setup default), or point FARM at a shared **Microsoft SQL Server** so a whole team works from one source of truth.
- **Multi-user with roles** — networked mode adds sign-in and role-based access: **Admin**, **Publisher**, **Scanner**, **Setup Builder**, and **QA Tester** — including a review/approval gate so releases can be signed off before they go public.

### Who it's for

FARM is aimed at **small teams, solo developers, and beginners** who want a professional installer and auto-updates without standing up update servers, CI pipelines, installer toolchains, or paid delivery services. If you can build your app and you have somewhere to host files, you have everything you need. The defaults are sensible, the workflow is step-by-step, and the on-disk formats are plain JSON and ZIP — nothing proprietary to lock you in or learn from scratch.

> **Built with AI.** FARM was designed and built with heavy use of AI assistance. It's a real, working tool — and also a demonstration that a capable, polished release manager can be created this way.

---

# ForgeTek Update Client Integration Guide

This guide explains how to integrate ForgeTek Application Release Manager-generated updates into your application. Use this documentation to implement automatic update checking, download, and installation for your app.

## Overview

ForgeTek Application Release Manager creates two core artifacts for each app release:
1. **Update Catalog JSON**: Publicly accessible JSON file listing all available versions and download URLs
2. **.ftu Update Package**: Binary container with your app files, integrity checks, and metadata

Your application will poll the update catalog, download new packages, verify them, and apply updates.

---

## Update Catalog Format

The update catalog is a JSON file hosted at a public URL. The standard path pattern is:
`{baseUrl}/{appKey}/{appKey}.json` (e.g., `https://example.com/releases/stlorganizer/stlorganizer.json`)

Note: The extension of the catalog file can be customized. While `.ftu` is the default, the tool supports custom extensions (e.g., `.stlo`). Update your catalog URL parsing to match.
Note 2: STLOrganizer is used as an example app name/key in this documentation. It will be replaced with your actual app name/key in your implementation.

### Catalog Structure
```json
{
  "stlorganizer": "0.1.2",
  "url": {
    "stlorganizer": "https://example.com/releases/stlorganizer/0.1.2/STLOrganizer-0.1.2.stlo"
  },
  "channels": {
    "stable": { "version": "0.1.2", "url": "https://example.com/releases/stlorganizer/0.1.2/STLOrganizer-0.1.2.stlo" },
    "beta":   { "version": "0.1.3", "url": "https://example.com/releases/stlorganizer/0.1.3/STLOrganizer-0.1.3.stlo" }
  },
  "latestFull": {
    "version": "0.1.0",
    "url": "https://example.com/releases/stlorganizer/0.1.0/STLOrganizer-0.1.0.stlo"
  },
  "versions": {
    "0.1.0": {
      "url": "https://example.com/releases/stlorganizer/0.1.0/STLOrganizer-0.1.0.stlo",
      "date": "2026-04-29",
      "type": "full",
      "base": "",
      "checksum": "...",
      "channel": "stable"
    },
    "0.1.2": {
      "url": "https://example.com/releases/stlorganizer/0.1.2/STLOrganizer-0.1.2.stlo",
      "date": "2026-04-30",
      "type": "incremental",
      "base": "0.1.0",
      "checksum": "64babf2a746b9dbc234fa0bbdbd1dc3b25e1914fdbe1f05f626d8fcf499a80af",
      "channel": "stable"
    }
  }
}
```

> **Release channels.** The top-level `{appKey}` pointer (and `url.{appKey}`) always tracks the latest **stable** release, so a stable-only client can simply follow it and ignore channels entirely. The `channels` object exposes the newest `stable` and `beta` releases separately — a beta-opt-in client should follow `channels.beta` (beta clients receive Beta + Stable; stable clients receive Stable only). Each `versions.{version}.channel` is `"stable"` or `"beta"`.

> **Cumulative incrementals (important).** Every incremental is **cumulative since the full baseline named in its `base`** — it contains *all* files changed since that baseline, not just since the previous patch. So an install at or after `base` can apply the single latest incremental and end up complete; intermediate patches never need to be applied in sequence.

### Field Descriptions
| Field | Type | Description |
|-------|------|-------------|
| `{appKey}` | string | Current latest **stable** version number for your app |
| `url.{appKey}` | string | Download URL for the latest stable package |
| `channels` | object | `{stable, beta}`, each `{version, url}` of the newest release on that channel. Stable-only clients can ignore this and follow `{appKey}`/`url.{appKey}`. |
| `latestFull` | object | `{version, url}` of the newest **full** baseline. A fresh install — or one older than the latest patch's `base` — must download this first. |
| `versions.{version}` | object | Per-version metadata |
| `versions.{version}.url` | string | Download URL for this specific version |
| `versions.{version}.date` | string | Release date (YYYY-MM-DD) |
| `versions.{version}.type` | string | `incremental` (cumulative since `base`) or `full` (all files) |
| `versions.{version}.base` | string | The full baseline this incremental is cumulative from (empty for a full). An install older than this must fetch `latestFull` first. |
| `versions.{version}.checksum` | string | SHA-256 checksum of the entire .ftu package |
| `versions.{version}.channel` | string | `stable` or `beta` |

### Choosing what to download (client logic)
```
installed = local app version (or none)
latest    = catalog[appKey]                       // newest version
base      = catalog.versions[latest].base         // "" if latest is a full

if installed is none OR installed < base:
    download + apply latestFull                    // get the baseline
    if latest != latestFull.version:
        download + apply latest                    // one cumulative hop
else:
    download + apply latest                        // single cumulative incremental
```
After applying, **verify the full expected file set** (see `ExpectedFiles` below) and self-heal if anything is missing.

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
The header is embedded in the package and contains metadata. Property names are camelCase; per-file
`checksum` is the **raw lowercase SHA-256 hex** (no `sha256-` prefix — unlike `manifest.json`, see below):
```json
{
  "appKey": "STLOrganizer",
  "version": "0.1.2",
  "packageType": "incremental",
  "baseVersion": "0.1.0",
  "createdAt": "2026-04-30T11:27:00.7854434+00:00",
  "fileCount": 2,
  "files": [
    { "path": "STLOrganizer.dll", "checksum": "90ec0ff137c65d222094cf20539f338e7553732263c1aa06b4350ead457a21bd" },
    { "path": "STLOrganizer.exe", "checksum": "92b573252aa76a50d8a687d14010197dc455ce6066a53e28bf41026ce69cd62f" }
  ],
  "expectedFiles": [
    { "path": "STLOrganizer.dll", "checksum": "90ec0ff137c65d222094cf20539f338e7553732263c1aa06b4350ead457a21bd" },
    { "path": "STLOrganizer.exe", "checksum": "92b573252aa76a50d8a687d14010197dc455ce6066a53e28bf41026ce69cd62f" },
    { "path": "STLOrganizer.staticwebassets.endpoints.json", "checksum": "02bdae549ea227de42bf50e7d894753d9a7a8f78c98c751b3dafb47749eb9c05" }
  ],
  "removedFiles": ["deprecated/config.json", "oldplugin.dll"]
}
```
`files` carries only what changed since `baseVersion` (everything, for a full); `expectedFiles` lists the
complete file set after applying (used for self-heal). `fileCount` counts the entries in the ZIP payload.

### Header Fields
| Field (JSON) | Type | Description |
|-------|------|-------------|
| `appKey` | string | Your application's name/app key |
| `version` | string | Package version number |
| `packageType` | string | `incremental` or `full` |
| `baseVersion` | string | The full baseline a cumulative incremental is relative to (null/empty for a full) |
| `createdAt` | string | ISO 8601 timestamp of package creation |
| `fileCount` | int | Number of files in the ZIP payload |
| `files` | array | Files **in the ZIP payload** (changed since the baseline). Each is `{path, checksum}` where `checksum` is the raw SHA-256 hex. |
| `expectedFiles` | array | The **full expected file set** after applying (every non-debug file as `{path, checksum}`). Use it to verify the install is complete and self-heal — if any expected file is missing or its hash doesn't match, download and apply `latestFull`. |
| `removedFiles` | array | Relative paths of files to delete after extraction (baseline-relative; empty for full packages) |

---

## Manifest Format (manifest.json)

Each package includes a `manifest.json` (either embedded in the ZIP or hosted alongside the package) with file-level details:
```json
{
  "version": "0.1.2",
  "app": "STLOrganizer",
  "createdAt": "2026-04-30T11:27:00.7854434+00:00",
  "baseVersion": "0.1.0",
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
| `baseVersion` | string | The full baseline this version is relative to (null/empty for a full) |
| `files` | array | Files included in this update; each `{path, hash, size}` where `hash` is `sha256-<hex>` |
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
    // The header is camelCase JSON — deserialize case-insensitively (or use a camelCase naming policy).
    PackageHeader? header = JsonSerializer.Deserialize<PackageHeader>(headerJson,
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
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
    public string AppKey { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string PackageType { get; set; } = string.Empty;
    public string? BaseVersion { get; set; }                       // cumulative baseline (null for full)
    public DateTimeOffset CreatedAt { get; set; }
    public int FileCount { get; set; }
    public List<PackageHeaderFile> Files { get; set; } = [];          // ZIP payload (changed files)
    public List<PackageHeaderFile> ExpectedFiles { get; set; } = []; // full expected state (for self-heal)
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

- **Incremental packages** are **cumulative since `header.BaseVersion`** — applying the single latest incremental over any install at or after that baseline yields a complete app. Overwrite existing files with the payload, then delete any files listed in `header.RemovedFiles`.
- **Full packages**: replace all files (contains all non-debug application files). `RemovedFiles` is empty.
- **Always self-heal**: after applying, verify every entry in `header.ExpectedFiles` exists on disk with a matching SHA-256. If any is missing or wrong (e.g. the install was older than `BaseVersion`, or got here some other way), download and apply `latestFull`, then re-apply the latest incremental. This guarantees a complete install regardless of how the user arrived.

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

    // Self-heal: confirm the full expected file set is present, else fall back to the full baseline.
    foreach (var expected in header.ExpectedFiles ?? [])
    {
        var fullPath = Path.Combine(installDir, expected.Path.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(fullPath) || Sha256Hex(fullPath) != expected.Checksum)
        {
            // Missing/mismatched → download latestFull from the catalog and apply it, then the latest patch.
            await ApplyFullBaselineAndLatest(installDir);
            return;
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

- **Package Extensions**: While `.ftu` is the default, the tool supports custom extensions (e.g., `.stlo`). Update your catalog URL parsing to match.
- **Hosting**: Packages and catalogs can be hosted on any HTTP/HTTPS server, FTP (with direct links), or cloud storage with public access.
- **Security**: The SHA-256 footer ensures package integrity. For additional security, use HTTPS for all downloads and consider code signing your application executables.

---

## Support

For issues with ForgeTek Application Release Manager or this documentation, report to: dirtek@gmail.com
