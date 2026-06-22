using Microsoft.EntityFrameworkCore;
using ForgeTekApplicationReleaseManager.Data;
using ForgeTekApplicationReleaseManager.Models;
using ForgeTekApplicationReleaseManager.Services.Storage;

namespace ForgeTekApplicationReleaseManager.Services;

/// <summary>EF Core-backed approval store. The state-derivation logic lives in the static
/// <see cref="Evaluate"/> / <see cref="CountSatisfied"/> helpers so it's unit-testable without a DB.</summary>
public sealed class ApprovalService(IDbContextFactory<ForgeTekDbContext> factory) : IApprovalService
{
    public IReadOnlyList<ReleaseApproval> GetForTarget(string targetKey)
    {
        using var db = factory.CreateDbContext();
        return db.Approvals.AsNoTracking()
            .Where(a => a.TargetKey == targetKey)
            .OrderBy(a => a.TimestampUtc)
            .Select(a => a.Payload).ToList()
            .Select(EfJson.Deserialize<ReleaseApproval>).ToList();
    }

    public void Add(ReleaseApproval approval)
    {
        using var db = factory.CreateDbContext();
        db.Approvals.Add(new ApprovalRow
        {
            Id = approval.Id,
            TargetKey = approval.TargetKey,
            TimestampUtc = approval.TimestampUtc,
            Payload = EfJson.Serialize(approval),
        });
        db.SaveChanges();
    }

    public ApprovalState GetState(string targetKey) => Evaluate(GetForTarget(targetKey));

    public int ApprovalsSatisfied(string targetKey) => CountSatisfied(GetForTarget(targetKey));

    // ── pure derivation (no DB) ──────────────────────────────────────────
    /// <summary>Approved when there is a current Approve from an Admin AND from a QA Tester (each newer than
    /// any Reject); Rejected when the newest binding vote is a Reject; otherwise Pending. Notes are ignored.</summary>
    public static ApprovalState Evaluate(IReadOnlyList<ReleaseApproval> votes)
    {
        var (adminOk, qaOk) = Satisfied(votes, out var lastReject, out var lastApprove);
        if (adminOk && qaOk) return ApprovalState.Approved;
        if (lastReject is not null && (lastApprove is null || lastReject > lastApprove))
            return ApprovalState.Rejected;
        return ApprovalState.Pending;
    }

    /// <summary>Number of the two required approvals (Admin, QA Tester) currently satisfied.</summary>
    public static int CountSatisfied(IReadOnlyList<ReleaseApproval> votes)
    {
        var (adminOk, qaOk) = Satisfied(votes, out _, out _);
        return (adminOk ? 1 : 0) + (qaOk ? 1 : 0);
    }

    private static (bool AdminOk, bool QaOk) Satisfied(
        IReadOnlyList<ReleaseApproval> votes, out DateTime? lastReject, out DateTime? lastApprove)
    {
        var binding = votes.Where(v => v.Decision is ApprovalDecision.Approve or ApprovalDecision.Reject).ToList();
        var rejects = binding.Where(v => v.Decision == ApprovalDecision.Reject).ToList();
        var approves = binding.Where(v => v.Decision == ApprovalDecision.Approve).ToList();

        lastReject  = rejects.Count  > 0 ? rejects.Max(r => r.TimestampUtc)  : null;
        lastApprove = approves.Count > 0 ? approves.Max(a => a.TimestampUtc) : null;
        var floor = lastReject;

        bool adminOk = approves.Any(a => a.ByRole == UserRole.Admin    && (floor is null || a.TimestampUtc > floor));
        bool qaOk    = approves.Any(a => a.ByRole == UserRole.QaTester && (floor is null || a.TimestampUtc > floor));
        return (adminOk, qaOk);
    }
}
