using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ForgeTekUpdatePackager.Models;

public enum PackageStep { Sign, Manifest, Package, Json, Ftp }

public enum VersionStatus { Review, Signed, Packed, Published, Retracted, Scrapped }

/// <summary>Release channel a version is published to. Beta clients receive Beta + Stable; Stable clients receive Stable only.</summary>
public enum UpdateChannel { Stable, Beta }

/// <summary>Whether the package contains all non-debug files or only the diff (added+modified).</summary>
public enum PackageType { Incremental, Full }

public partial class AppVersion : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string VersionNumber { get; set; } = string.Empty;
    public DateTime ScanDate { get; set; }

    /// <summary>When this version was published (status reached Published). Null until published.</summary>
    public DateTime? PublishedDate { get; set; }

    /// <summary>Who published it — the signed-in user, or the Windows user when unprotected.</summary>
    public string? PublishedBy { get; set; }

    /// <summary>Release channel. Stable by default; Beta marks a pre-release that only beta clients receive.</summary>
    public UpdateChannel Channel { get; set; } = UpdateChannel.Stable;
    public List<FileRecord> Files { get; set; } = [];

    /// <summary>True for the very first version — no diff, no packing needed.</summary>
    // Observable so the status badge DataTrigger on IsInitial refreshes in the DataGrid.
    [ObservableProperty] private bool _isInitial;

    // Observable so the status badge DataTrigger refreshes whenever Status changes
    // (e.g. after a retract resets Published → Packed).
    [ObservableProperty] private VersionStatus _status = VersionStatus.Review;

    public bool HasManifest { get; set; }
    public bool HasPackage { get; set; }
    public string? PackagePath { get; set; }
    public string? PackageChecksum { get; set; }
    public PackageType PackageType { get; set; } = PackageType.Incremental;

    /// <summary>For a cumulative incremental, the full baseline version it was built against (its
    /// payload carries every file changed since this baseline). Null when this version is itself Full.</summary>
    public string? BaseVersion { get; set; }

    /// <summary>Tracks the last completed pipeline step so packaging can resume.</summary>
    public PackageStep? PipelineStep { get; set; }

    /// <summary>Transport used to publish this version ("Ftp"/"Sftp"/"S3"/"GitHubReleases"). Drives retract.</summary>
    public string? PublishProvider { get; set; }

    /// <summary>FTP remote path of the uploaded .ftu package file — set after a successful upload.</summary>
    public string? FtpPackageRemotePath { get; set; }
    /// <summary>FTP remote path of the uploaded catalog JSON — set after a successful upload.</summary>
    public string? FtpCatalogRemotePath { get; set; }
    /// <summary>FTP host used for the upload — needed to delete files on retract.</summary>
    public string? FtpHost { get; set; }
    public int FtpPort { get; set; }
    public string? FtpUsername { get; set; }
    public string? FtpPassword { get; set; }

    [JsonIgnore]
    public bool HasChangelog { get; set; }

    /// <summary>When a setup bundle that shipped this version was most recently generated. Populated
    /// from setup history when the version list is built; null if no setup has shipped this version.</summary>
    [JsonIgnore]
    public DateTime? SetupGeneratedDate { get; set; }

    public bool HasDiff { get; set; }
    public int AddedCount { get; set; }
    public int ModifiedCount { get; set; }
    public int RemovedCount { get; set; }

    /// <summary>Relative paths of files deleted in this version's diff — used by incremental packages to signal clients to remove obsolete files.</summary>
    public List<string> RemovedFiles { get; set; } = [];

    /// <summary>Files actually shipped in the package — excludes both excluded (debug) and removed files.</summary>
    public IEnumerable<FileRecord> NonDebugFiles => Files.Where(f => !f.IsDebug && !f.IsRemoved);
    /// <summary>Files the user explicitly marked for deletion on clients.</summary>
    public IEnumerable<FileRecord> RemovedMarkedFiles => Files.Where(f => f.IsRemoved);
    public int TotalFiles => Files.Count;
    public int DebugFileCount => Files.Count(f => f.IsDebug);
    public int PackageFileCount => Files.Count(f => !f.IsDebug && !f.IsRemoved);

    /// <summary>"Full" or "Incremental" for the version list (the initial scan is a full baseline).</summary>
    [JsonIgnore]
    public string PackageKindLabel => IsInitial || PackageType == PackageType.Full ? "Full" : "Incremental";
}
