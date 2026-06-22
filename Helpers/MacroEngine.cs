using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ForgeTekApplicationReleaseManager.Helpers;

/// <summary>
/// Resolves <c>{Variable}</c> build-variable tokens in user-supplied templates (file names, setup
/// fields, release notes). Date/time tokens accept an optional .NET format: <c>{Date:yyyyMMdd}</c>.
/// Unknown tokens are left intact so a stray brace never silently deletes text.
/// </summary>
public static partial class MacroEngine
{
    [GeneratedRegex(@"\{([A-Za-z_][A-Za-z0-9_]*)(?::([^}]+))?\}")]
    private static partial Regex TokenRegex();

    /// <summary>The variable names available to users (for UI hints).</summary>
    public const string VariableHint =
        "Variables: {AppName} {Version} {Channel} {Company} {Date} {Year} {Date:format}";

    public static string Resolve(string? template, IReadOnlyDictionary<string, string> vars, DateTime? now = null)
    {
        if (string.IsNullOrEmpty(template)) return template ?? string.Empty;
        var ts = now ?? DateTime.Now;

        return TokenRegex().Replace(template, m =>
        {
            var key = m.Groups[1].Value;
            var fmt = m.Groups[2].Success ? m.Groups[2].Value : null;

            if (key.Equals("Date", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("Now", StringComparison.OrdinalIgnoreCase))
                return ts.ToString(fmt ?? "yyyy-MM-dd");
            if (key.Equals("Time", StringComparison.OrdinalIgnoreCase))
                return ts.ToString(fmt ?? "HH-mm");
            if (key.Equals("Year", StringComparison.OrdinalIgnoreCase))
                return ts.ToString(fmt ?? "yyyy");

            return vars.TryGetValue(key, out var value) ? value : m.Value;
        });
    }

    /// <summary>Standard variable set shared across setups and the package pipeline.</summary>
    public static Dictionary<string, string> StandardVars(
        string appName, string? version, string? channel, string? company)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["AppName"] = appName,
            ["Name"]    = appName,
            ["Version"] = version ?? string.Empty,
            ["Channel"] = channel ?? string.Empty,
            ["Company"] = company ?? string.Empty,
        };
}
