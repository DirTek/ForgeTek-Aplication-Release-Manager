namespace ForgeTekUpdatePackager.Models;

/// <summary>Allow / warn / block lists for third-party licenses (reuses <see cref="PolicyAction"/>).
/// Matching is token-based against SPDX identifiers so "MIT OR GPL-3.0" blocks on the GPL token while
/// "LGPL-3.0-only" only warns.</summary>
public sealed class LicensePolicy
{
    /// <summary>SPDX prefixes treated as allowed (e.g. "MIT", "Apache", "BSD").</summary>
    public List<string> Allowed { get; set; } = ["MIT", "Apache", "BSD", "ISC", "MS-PL", "MS-RL", "0BSD", "Zlib", "Unlicense", "BSL", "CC0", "Python"];

    /// <summary>SPDX prefixes treated as warnings (weak copyleft).</summary>
    public List<string> Warn { get; set; } = ["LGPL", "MPL", "EPL", "CDDL", "MS-EULA"];

    /// <summary>SPDX prefixes treated as blocking (strong copyleft / restrictive).</summary>
    public List<string> Block { get; set; } = ["GPL", "AGPL", "SSPL", "CC-BY-NC", "BUSL"];

    /// <summary>Action for components whose license couldn't be identified.</summary>
    public PolicyAction Unknown { get; set; } = PolicyAction.Warn;

    /// <summary>Classifies a license string. Multi-license expressions take the worst matched action.</summary>
    public PolicyAction ActionFor(string? license)
    {
        if (string.IsNullOrWhiteSpace(license)) return Unknown;
        if (string.Equals(license, "Unknown", StringComparison.OrdinalIgnoreCase)) return Unknown;

        var tokens = license.Split([' ', '(', ')', ',', '/', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        PolicyAction? worst = null;
        foreach (var token in tokens)
        {
            // Skip SPDX operators.
            if (token is "OR" or "AND" or "WITH") continue;
            var action = Classify(token);
            if (action is { } a && (worst is null || a > worst)) worst = a;
        }
        return worst ?? Unknown;
    }

    private PolicyAction? Classify(string token)
    {
        // Block/Warn before Allowed; longer prefixes win (AGPL before… not needed since StartsWith).
        if (Block.Any(b => token.StartsWith(b, StringComparison.OrdinalIgnoreCase))) return PolicyAction.Block;
        if (Warn.Any(w => token.StartsWith(w, StringComparison.OrdinalIgnoreCase))) return PolicyAction.Warn;
        if (Allowed.Any(a => token.StartsWith(a, StringComparison.OrdinalIgnoreCase))) return PolicyAction.Allow;
        return null;   // token matched nothing (operator / unrecognized)
    }

    /// <summary>The worst action across all components (Block &gt; Warn &gt; Allow).</summary>
    public PolicyAction Evaluate(LicenseReport report)
    {
        var worst = PolicyAction.Allow;
        foreach (var c in report.Components)
        {
            var action = ActionFor(c.License);
            if (action > worst) worst = action;
        }
        return worst;
    }
}
