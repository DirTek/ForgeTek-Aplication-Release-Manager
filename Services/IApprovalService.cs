using ForgeTekUpdatePackager.Models;

namespace ForgeTekUpdatePackager.Services;

/// <summary>Records and evaluates release sign-offs. A release is publishable only when its
/// <see cref="GetState"/> is <see cref="ApprovalState.Approved"/> (an Admin and a QA Tester have both
/// approved, with no newer rejection). Notes are recorded but never affect state.</summary>
public interface IApprovalService
{
    /// <summary>All votes/notes on a target, oldest first.</summary>
    IReadOnlyList<ReleaseApproval> GetForTarget(string targetKey);

    /// <summary>Append a vote or note (immutable).</summary>
    void Add(ReleaseApproval approval);

    /// <summary>Derived approval state for a target.</summary>
    ApprovalState GetState(string targetKey);

    /// <summary>True when the target is approved and may be published.</summary>
    bool IsApproved(string targetKey) => GetState(targetKey) == ApprovalState.Approved;

    /// <summary>How many of the two required approvals (Admin, QA Tester) are currently satisfied — for
    /// "1 of 2"-style UI hints.</summary>
    int ApprovalsSatisfied(string targetKey);
}
