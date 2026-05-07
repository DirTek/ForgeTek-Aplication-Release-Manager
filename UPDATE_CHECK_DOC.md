# STLOrganizer Update Check Mechanism

## Overview

STLOrganizer checks for new versions by comparing its local file version against a remote JSON manifest hosted on `usas.forgetek.ro`. This is orchestrated by the `UpdateCheckService` in STLVerse, which runs as a background service and is also triggerable from the Home dashboard UI.

---

## Step-by-Step Flow

### 1. Determining the Local Version

The current installed version of `STLOrganizer.exe` is read from its **file version metadata** using `FileVersionInfo.GetVersionInfo()`:

- The executable path is stored in the Windows registry at `HKCU\SOFTWARE\ForgeTek Solutions\STLOrganizer\ExecutablePath`.
- `FileVersion` is read and trimmed to 3 components: `major.minor.build`.
- Example: If `FileVersion` is `1.2.3.4`, the version becomes `1.2.3`.

If the registry path is missing or the version cannot be read, the version defaults to `"Unknown"` (no update will be reported).

### 2. Fetching the Remote Manifest

An HTTP GET request is sent to:

```
https://usas.forgetek.ro/aplicatii/testorganizer/stlorganizer/stlorganizer.json
```

The expected JSON response shape:

```json
{
  "stlorganizer": "0.1.2",
  "url": {
    "stlorganizer": "https://example.com/STLOrganizer-0.1.2.stlo"
  },
  "versions": {
    "0.1.1": {
      "url": "https://...",
      "date": "2026-04-30",
      "type": "incremental",
      "checksum": "0412bca9..."
    }
  }
}
```

The service extracts:
- `root.stlorganizer` — the latest available version string.
- `root.url.stlorganizer` — the download URL for the update package (`.stlo` file).

On success, these values are cached to MAUI `Preferences` (keys: `STLOrgLatestVersion`, `STLOrgPackageUrl`).

On failure (network error, invalid JSON), the previously cached values are retained so the UI still shows the last known available version.

### 3. Cache Policy

To avoid hitting the server too often, the manifest is cached for **6 hours**:
- The timestamp of the last API call is stored in `Preferences` under `STLOrgLastApiCheck`.
- A new fetch only occurs if 6+ hours have elapsed since the last check, or if `force: true` is passed (e.g., when the user manually clicks "Check for Updates").
- A `System.Threading.Timer` fires every 6 hours to perform background checks.
- The Home page also triggers a non-forced check on load.

### 4. Version Comparison

The fetched latest version is compared against the local version using `System.Version`:

```csharp
private static bool IsNewerVersion(string? latest, string? current)
{
    if (string.IsNullOrWhiteSpace(latest) || string.IsNullOrWhiteSpace(current))
        return false;
    if (Version.TryParse(latest, out var l) && Version.TryParse(current, out var c))
        return l > c;
    return string.Compare(latest, current, StringComparison.Ordinal) > 0;
}
```

- Both strings are parsed as `System.Version` (supports `major.minor[.build[.revision]]`).
- If `latest > current`, an update is available.
- If either string cannot be parsed, it falls back to ordinal (lexicographic) string comparison.

### 5. User Notification

When a newer version is detected:
- `UpdateCheckService.UpdateAvailable` is set to `true`.
- A system tray balloon notification is shown: *"An update for STLOrganizer is available for downloading."*
- Duplicate notifications are suppressed by tracking `_notifiedVersion`.

---

## Trigger Points

| Trigger | Method | Force? | When |
|---------|--------|--------|------|
| Periodic timer | `StartPeriodicChecks()` | No | Every 6 hours after app start |
| Home page loaded | `Home.razor.OnInitializedAsync()` | No | On navigating to the dashboard |
| User action | UI button | Yes | On demand |

---

## Related Files

| File | Role |
|------|------|
| `Services/UpdateCheckService.cs` | Core update check logic for STLOrganizer |
| `Services/STLVerseUpdateService.cs` | Same pattern for STLVerse self-update |
| `Services/ForgeTekPackageParser.cs` | Parses and verifies update packages (`.stlo`/`.stlv`) |
| `Services/RegistryService.cs` | Reads STLOrganizer install path from registry |
| `Services/STLOrganizerLaunchService.cs` | Stops/restarts STLOrganizer during updates |
| `Services/TrayIconService.cs` | Shows balloon notifications |
| `Components/Pages/Home.razor` | Dashboard UI that triggers and displays update status |
| `MauiProgram.cs` | DI registration and periodic timer startup |
