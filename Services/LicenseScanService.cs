using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using ForgeTekApplicationReleaseManager.Models;

namespace ForgeTekApplicationReleaseManager.Services;

/// <summary>Lists a project's NuGet dependencies (`dotnet list package --include-transitive`) and
/// resolves each package's license from its nuspec on nuget.org, producing a Third-Party Components
/// report. Requires the .NET SDK on PATH; license lookups need network access.</summary>
public sealed class LicenseScanService : ILicenseScanService
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("ForgeTek-Release-Manager");
        return c;
    }

    public async Task<LicenseReport> ScanAsync(string projectOrFolder,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var target = ProjectLocator.Resolve(projectOrFolder);
        if (target is null)
            return new LicenseReport { Error = "No .sln or .csproj found at the given path.", ScannedPath = projectOrFolder };

        var workingDir = System.IO.Path.GetDirectoryName(target) ?? projectOrFolder;
        var args = $"list \"{target}\" package --include-transitive --format json";

        try
        {
            progress?.Report($"> dotnet {args}");
            var result = await ProcessRunner.RunAsync("dotnet", args, workingDir, progress, ct);
            if (NeedsRestore(result.Output))
            {
                progress?.Report("> dotnet restore (assets file missing)…");
                await ProcessRunner.RunAsync("dotnet", $"restore \"{target}\"", workingDir, progress, ct);
                result = await ProcessRunner.RunAsync("dotnet", args, workingDir, progress, ct);
            }

            var json = ExtractJson(result.Output);
            if (json is null)
                return new LicenseReport
                {
                    Error = result.Succeeded ? "Could not read the package list." : result.Output.Trim(),
                    ScannedPath = target,
                };

            var packages = ParsePackages(json);
            var components = new List<LicenseComponent>();
            var index = 0;
            foreach (var (id, version, transitive) in packages)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report($"Resolving license {++index}/{packages.Count}: {id}");
                var (license, licenseUrl, projectUrl) = await ResolveLicenseAsync(id, version, ct);
                components.Add(new LicenseComponent(id, version, license, licenseUrl, projectUrl, transitive));
            }

            return new LicenseReport
            {
                Components = components.OrderBy(c => c.Id, StringComparer.OrdinalIgnoreCase).ToList(),
                ScannedPath = target,
            };
        }
        catch (ToolNotFoundException)
        {
            return new LicenseReport { Error = "The .NET SDK (dotnet) was not found on PATH.", ScannedPath = target };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new LicenseReport { Error = ex.Message, ScannedPath = target };
        }
    }

    // ── NuGet license lookup ────────────────────────────────────────────────

    private async Task<(string License, string LicenseUrl, string ProjectUrl)> ResolveLicenseAsync(
        string id, string version, CancellationToken ct)
    {
        try
        {
            var lower = id.ToLowerInvariant();
            var v = version.ToLowerInvariant();
            var url = $"https://api.nuget.org/v3-flatcontainer/{lower}/{v}/{lower}.nuspec";
            using var resp = await Http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return ("Unknown", "", "");
            var xml = await resp.Content.ReadAsStringAsync(ct);
            return ParseLicenseFromNuspec(xml);
        }
        catch (OperationCanceledException) { throw; }
        catch { return ("Unknown", "", ""); }
    }

    /// <summary>Extracts (license, licenseUrl, projectUrl) from a package's nuspec XML.</summary>
    public static (string License, string LicenseUrl, string ProjectUrl) ParseLicenseFromNuspec(string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            XElement? Find(string name) => doc.Descendants().FirstOrDefault(e => e.Name.LocalName == name);

            var licenseUrl = Find("licenseUrl")?.Value.Trim() ?? "";
            var projectUrl = Find("projectUrl")?.Value.Trim() ?? "";

            var licenseEl = Find("license");
            string license;
            if (licenseEl is not null)
            {
                var type = licenseEl.Attribute("type")?.Value;
                license = string.Equals(type, "file", StringComparison.OrdinalIgnoreCase)
                    ? "Custom (file)"
                    : licenseEl.Value.Trim();
            }
            else
            {
                license = SpdxFromUrl(licenseUrl);
            }

            if (string.IsNullOrWhiteSpace(license)) license = "Unknown";
            return (license, licenseUrl, projectUrl);
        }
        catch { return ("Unknown", "", ""); }
    }

    // nuget.org rewrote legacy licenseUrls to https://licenses.nuget.org/<SPDX>.
    private static string SpdxFromUrl(string licenseUrl)
    {
        if (string.IsNullOrWhiteSpace(licenseUrl)) return "Unknown";
        const string marker = "licenses.nuget.org/";
        var idx = licenseUrl.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            var spdx = licenseUrl[(idx + marker.Length)..].Trim('/');
            if (!string.IsNullOrWhiteSpace(spdx)) return spdx;
        }
        return "Unknown";
    }

    // ── Package list parsing ────────────────────────────────────────────────

    /// <summary>Parses `dotnet list package --include-transitive --format json` into (id, version, transitive),
    /// deduped by id (top-level preferred).</summary>
    public static List<(string Id, string Version, bool Transitive)> ParsePackages(string json)
    {
        var byId = new Dictionary<string, (string Version, bool Transitive)>(StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("projects", out var projects))
            return [];

        foreach (var project in projects.EnumerateArray())
        {
            if (!project.TryGetProperty("frameworks", out var frameworks)) continue;
            foreach (var fw in frameworks.EnumerateArray())
            {
                Collect(fw, "topLevelPackages", transitive: false, byId);
                Collect(fw, "transitivePackages", transitive: true, byId);
            }
        }

        return byId.Select(kv => (kv.Key, kv.Value.Version, kv.Value.Transitive)).ToList();
    }

    private static void Collect(JsonElement framework, string property, bool transitive,
        Dictionary<string, (string Version, bool Transitive)> sink)
    {
        if (!framework.TryGetProperty(property, out var pkgs)) return;
        foreach (var pkg in pkgs.EnumerateArray())
        {
            var id = pkg.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(id)) continue;
            var version = pkg.TryGetProperty("resolvedVersion", out var v) ? v.GetString() ?? "" : "";

            // Top-level wins over transitive for the same id.
            if (!sink.TryGetValue(id, out var existing) || (existing.Transitive && !transitive))
                sink[id] = (version, transitive);
        }
    }

    // ── Report writers ──────────────────────────────────────────────────────

    public string BuildText(LicenseReport report, string subject)
    {
        var sb = new StringBuilder();
        sb.AppendLine("THIRD-PARTY COMPONENTS");
        sb.AppendLine();
        sb.AppendLine($"{subject} includes the following third-party NuGet packages and their licenses.");
        sb.AppendLine($"Generated {DateTime.Now:yyyy-MM-dd}.");
        sb.AppendLine();
        foreach (var c in report.Components)
        {
            sb.AppendLine($"{c.Id} {c.Version}");
            sb.AppendLine($"  License: {c.License}");
            if (!string.IsNullOrWhiteSpace(c.ProjectUrl)) sb.AppendLine($"  Project: {c.ProjectUrl}");
            if (!string.IsNullOrWhiteSpace(c.LicenseUrl)) sb.AppendLine($"  License URL: {c.LicenseUrl}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public string BuildHtml(LicenseReport report, string subject)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html><head><meta charset=\"utf-8\">");
        sb.AppendLine($"<title>Third-Party Components — {Esc(subject)}</title>");
        sb.AppendLine("<style>body{font-family:Segoe UI,Arial,sans-serif;margin:2rem;color:#1c1c1e}" +
                      "h1{font-size:1.4rem}table{border-collapse:collapse;width:100%}" +
                      "th,td{text-align:left;padding:.5rem .75rem;border-bottom:1px solid #ddd;font-size:.9rem}" +
                      "th{background:#f4f4f5}a{color:#0a84ff;text-decoration:none}</style></head><body>");
        sb.AppendLine($"<h1>Third-Party Components</h1>");
        sb.AppendLine($"<p>{Esc(subject)} includes the following third-party NuGet packages. Generated {DateTime.Now:yyyy-MM-dd}.</p>");
        sb.AppendLine("<table><thead><tr><th>Package</th><th>Version</th><th>License</th><th>Project</th></tr></thead><tbody>");
        foreach (var c in report.Components)
        {
            var project = string.IsNullOrWhiteSpace(c.ProjectUrl) ? ""
                : $"<a href=\"{Esc(c.ProjectUrl)}\">link</a>";
            sb.AppendLine($"<tr><td>{Esc(c.Id)}</td><td>{Esc(c.Version)}</td><td>{Esc(c.License)}</td><td>{project}</td></tr>");
        }
        sb.AppendLine("</tbody></table></body></html>");
        return sb.ToString();
    }

    private static string Esc(string s) => System.Net.WebUtility.HtmlEncode(s);

    // ── Shared helpers ──────────────────────────────────────────────────────

    private static bool NeedsRestore(string output)
        => output.Contains("run a restore", StringComparison.OrdinalIgnoreCase)
        || output.Contains("run a NuGet package restore", StringComparison.OrdinalIgnoreCase)
        || output.Contains("No assets file", StringComparison.OrdinalIgnoreCase);

    private static string? ExtractJson(string output)
    {
        var start = output.IndexOf('{');
        var end = output.LastIndexOf('}');
        return start >= 0 && end > start ? output[start..(end + 1)] : null;
    }
}
