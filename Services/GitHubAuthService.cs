using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ForgeTekUpdatePackager.Services;

/// <summary>
/// GitHub OAuth Device Flow client. Desktop apps can't safely hold a client secret, so device flow
/// is used: request a code, the user authorizes in the browser, then we poll for the access token.
/// Endpoints live on github.com; the user lookup is on api.github.com.
/// </summary>
public class GitHubAuthService : IGitHubAuthService
{
    private const string GrantType = "urn:ietf:params:oauth:grant-type:device_code";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("ForgeTek-Release-Manager");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return c;
    }

    public async Task<DeviceCodeInfo> RequestDeviceCodeAsync(string clientId, string scope, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            throw new InvalidOperationException("No GitHub OAuth App Client ID configured.");

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId.Trim(),
            ["scope"]     = scope,
        });
        using var resp = await Http.PostAsync("https://github.com/login/device/code", content, ct);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var err))
            throw new InvalidOperationException(Describe(err.GetString(), root));

        return new DeviceCodeInfo(
            root.GetProperty("device_code").GetString() ?? "",
            root.GetProperty("user_code").GetString() ?? "",
            root.GetProperty("verification_uri").GetString() ?? "https://github.com/login/device",
            root.TryGetProperty("interval", out var iv) ? iv.GetInt32() : 5,
            root.TryGetProperty("expires_in", out var ex) ? ex.GetInt32() : 900);
    }

    public async Task<string> PollForTokenAsync(string clientId, string deviceCode, int intervalSeconds, CancellationToken ct = default)
    {
        var interval = Math.Max(intervalSeconds, 1);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(interval), ct);

            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"]   = clientId.Trim(),
                ["device_code"] = deviceCode,
                ["grant_type"]  = GrantType,
            });
            using var resp = await Http.PostAsync("https://github.com/login/oauth/access_token", content, ct);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;

            if (root.TryGetProperty("access_token", out var tok) && tok.GetString() is { Length: > 0 } token)
                return token;

            var error = root.TryGetProperty("error", out var e) ? e.GetString() : null;
            switch (error)
            {
                case "authorization_pending":
                    continue;                       // user hasn't finished yet — keep waiting
                case "slow_down":
                    interval += 5;                  // back off as instructed
                    continue;
                case "expired_token":
                    throw new InvalidOperationException("The code expired before authorization. Please try again.");
                case "access_denied":
                    throw new InvalidOperationException("Authorization was denied.");
                default:
                    throw new InvalidOperationException(Describe(error, root));
            }
        }
    }

    public async Task<string> GetLoginAsync(string token, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var resp = await Http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.TryGetProperty("login", out var l) ? l.GetString() ?? "" : "";
    }

    private static string Describe(string? error, JsonElement root)
    {
        var desc = root.TryGetProperty("error_description", out var d) ? d.GetString() : null;
        return $"GitHub error: {desc ?? error ?? "unknown"}.";
    }
}
