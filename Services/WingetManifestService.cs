using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ForgeTekUpdatePackager.Services;

/// <summary>Generates winget multi-file manifests (schema v1.6) for a generated installer.
/// YAML is hand-built (it's simple, order-sensitive, and needs the schema header comment), so no
/// YAML dependency is required.</summary>
public sealed partial class WingetManifestService : IWingetManifestService
{
    private const string ManifestVersion = "1.6.0";
    private const string SchemaBase = "https://aka.ms/winget-manifest";

    public string DeriveIdentifier(string publisher, string packageName)
        => $"{SanitizeIdPart(publisher)}.{SanitizeIdPart(packageName)}";

    public IReadOnlyDictionary<string, string> BuildYaml(WingetManifestInput input)
    {
        var id = input.PackageIdentifier;
        var files = new Dictionary<string, string>
        {
            [$"{id}.yaml"] = BuildVersion(input),
            [$"{id}.installer.yaml"] = BuildInstaller(input),
            [$"{id}.locale.{input.DefaultLocale}.yaml"] = BuildLocale(input),
        };
        return files;
    }

    public string Write(WingetManifestInput input, string outputRoot)
    {
        var folder = ManifestFolder(input.PackageIdentifier, input.Version, outputRoot);
        Directory.CreateDirectory(folder);
        foreach (var (name, content) in BuildYaml(input))
            File.WriteAllText(Path.Combine(folder, name), content, new UTF8Encoding(false));
        return folder;
    }

    // manifests/{firstLetterLower}/{id-part}/{id-part}/.../{version}/
    private static string ManifestFolder(string id, string version, string outputRoot)
    {
        var firstLetter = char.ToLowerInvariant(id.TrimStart('.')[0]).ToString();
        var parts = id.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var segments = new List<string> { outputRoot, "manifests", firstLetter };
        segments.AddRange(parts);
        segments.Add(version);
        return Path.Combine([.. segments]);
    }

    // ── Document builders ─────────────────────────────────────────────────

    private static string BuildVersion(WingetManifestInput x)
    {
        var sb = new StringBuilder();
        Header(sb, "version");
        sb.AppendLine($"PackageIdentifier: {Scalar(x.PackageIdentifier)}");
        sb.AppendLine($"PackageVersion: {Scalar(x.Version)}");
        sb.AppendLine($"DefaultLocale: {Scalar(x.DefaultLocale)}");
        sb.AppendLine("ManifestType: version");
        sb.AppendLine($"ManifestVersion: {ManifestVersion}");
        return sb.ToString();
    }

    private static string BuildInstaller(WingetManifestInput x)
    {
        var sb = new StringBuilder();
        Header(sb, "installer");
        sb.AppendLine($"PackageIdentifier: {Scalar(x.PackageIdentifier)}");
        sb.AppendLine($"PackageVersion: {Scalar(x.Version)}");
        sb.AppendLine($"InstallerType: {Scalar(x.InstallerType)}");
        sb.AppendLine("Installers:");
        sb.AppendLine($"  - Architecture: {Scalar(x.Architecture)}");
        sb.AppendLine($"    InstallerUrl: {Scalar(x.InstallerUrl)}");
        sb.AppendLine($"    InstallerSha256: {x.InstallerSha256.ToUpperInvariant()}");
        if (!string.IsNullOrWhiteSpace(x.SilentSwitch) || !string.IsNullOrWhiteSpace(x.SilentWithProgressSwitch))
        {
            sb.AppendLine("    InstallerSwitches:");
            if (!string.IsNullOrWhiteSpace(x.SilentSwitch))
                sb.AppendLine($"      Silent: {Scalar(x.SilentSwitch!)}");
            if (!string.IsNullOrWhiteSpace(x.SilentWithProgressSwitch))
                sb.AppendLine($"      SilentWithProgress: {Scalar(x.SilentWithProgressSwitch!)}");
        }
        sb.AppendLine("ManifestType: installer");
        sb.AppendLine($"ManifestVersion: {ManifestVersion}");
        return sb.ToString();
    }

    private static string BuildLocale(WingetManifestInput x)
    {
        var sb = new StringBuilder();
        Header(sb, "defaultLocale");
        sb.AppendLine($"PackageIdentifier: {Scalar(x.PackageIdentifier)}");
        sb.AppendLine($"PackageVersion: {Scalar(x.Version)}");
        sb.AppendLine($"PackageLocale: {Scalar(x.DefaultLocale)}");
        sb.AppendLine($"Publisher: {Scalar(x.Publisher)}");
        AppendIf(sb, "PublisherUrl", x.PublisherUrl);
        AppendIf(sb, "PublisherSupportUrl", x.PublisherSupportUrl);
        AppendIf(sb, "PackageUrl", x.PackageUrl);
        sb.AppendLine($"PackageName: {Scalar(x.PackageName)}");
        // License + ShortDescription are required by winget; fall back so the manifest validates.
        sb.AppendLine($"License: {Scalar(string.IsNullOrWhiteSpace(x.License) ? "Proprietary" : x.License!)}");
        AppendIf(sb, "LicenseUrl", x.LicenseUrl);
        if (!string.IsNullOrWhiteSpace(x.Moniker))
            sb.AppendLine($"Moniker: {Scalar(x.Moniker!)}");
        sb.AppendLine($"ShortDescription: {Scalar(string.IsNullOrWhiteSpace(x.ShortDescription) ? x.PackageName : x.ShortDescription!)}");
        AppendIf(sb, "Description", x.Description);
        if (x.Tags is { Count: > 0 })
        {
            sb.AppendLine("Tags:");
            foreach (var tag in x.Tags.Where(t => !string.IsNullOrWhiteSpace(t)))
                sb.AppendLine($"  - {Scalar(tag.Trim())}");
        }
        sb.AppendLine("ManifestType: defaultLocale");
        sb.AppendLine($"ManifestVersion: {ManifestVersion}");
        return sb.ToString();
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static void Header(StringBuilder sb, string kind)
    {
        sb.AppendLine($"# yaml-language-server: $schema={SchemaBase}.{kind}.{ManifestVersion}.schema.json");
        sb.AppendLine();
    }

    private static void AppendIf(StringBuilder sb, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            sb.AppendLine($"{key}: {Scalar(value!.Trim())}");
    }

    // Quotes a scalar (single-quoted YAML) when it contains characters that could break parsing.
    private static string Scalar(string value)
    {
        var v = value ?? string.Empty;
        var needsQuote = v.Length == 0
            || v != v.Trim()
            || "#:,&*?|<>=!%@`\"'{}[]".Any(v.Contains)
            || v.StartsWith('-');
        return needsQuote ? $"'{v.Replace("'", "''")}'" : v;
    }

    // Strips spaces and characters not allowed in a winget identifier segment.
    private static string SanitizeIdPart(string value)
    {
        var cleaned = IdPartInvalid().Replace(value ?? string.Empty, string.Empty);
        cleaned = cleaned.Trim('.', '-');
        return string.IsNullOrEmpty(cleaned) ? "App" : cleaned;
    }

    [GeneratedRegex(@"[^A-Za-z0-9\-]")]
    private static partial Regex IdPartInvalid();
}
