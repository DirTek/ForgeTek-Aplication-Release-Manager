namespace ForgeTekUpdatePackager.Models;

/// <summary>A binding vote or a non-binding note cast on a release.</summary>
public enum ApprovalDecision
{
    /// <summary>Binding approval (counts toward the Admin + QA Tester requirement).</summary>
    Approve,
    /// <summary>Binding rejection — blocks the release and bounces it back for changes.</summary>
    Reject,
    /// <summary>Non-binding review note — recorded for the audit trail, never affects state.</summary>
    Note,
}

/// <summary>Derived state of a release target from its votes.</summary>
public enum ApprovalState
{
    /// <summary>Not yet approved by both an Admin and a QA Tester (and not freshly rejected).</summary>
    Pending,
    /// <summary>Has a current Approve from an Admin and from a QA Tester, with no newer Reject.</summary>
    Approved,
    /// <summary>The most recent binding vote is a Reject — release is blocked.</summary>
    Rejected,
}

/// <summary>
/// One immutable approval/reject/note on a release (an app version update, or a generated setup). Append-only;
/// the thread of these for a <see cref="TargetKey"/> derives the <see cref="ApprovalState"/>. Stamped with who
/// voted, their role, and when — a cross-operator audit trail.
/// </summary>
public class ReleaseApproval
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Stable key grouping all votes on one release target. Build with
    /// <see cref="ForApp"/> / <see cref="ForSetup"/>.</summary>
    public string TargetKey { get; set; } = string.Empty;

    public ApprovalDecision Decision { get; set; }
    public string? Note { get; set; }
    public string ByUser { get; set; } = string.Empty;
    public UserRole ByRole { get; set; }
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Target key for an app version update.</summary>
    public static string ForApp(string appId, string versionNumber) => $"app:{appId}:{versionNumber}";

    /// <summary>Target key for a generated setup (bundle + generation record).</summary>
    public static string ForSetup(string bundleId, string recordId) => $"setup:{bundleId}:{recordId}";
}
