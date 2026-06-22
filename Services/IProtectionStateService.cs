namespace ForgeTekApplicationReleaseManager.Services;

/// <summary>
/// A tamper-evident record (kept outside users.json) that access protection is enabled. Lets startup
/// detect when the user database was deleted out-of-band and fail closed instead of opening freely.
/// </summary>
public interface IProtectionStateService
{
    /// <summary>True when the marker is present and valid (DPAPI-decryptable).</summary>
    bool IsMarked { get; }

    /// <summary>Records that protection is enabled.</summary>
    void Mark();

    /// <summary>Removes the marker (protection legitimately disabled).</summary>
    void Clear();
}
