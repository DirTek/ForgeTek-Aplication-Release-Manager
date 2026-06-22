namespace ForgeTekApplicationReleaseManager.Services;

/// <summary>A pending device-flow authorization: show <see cref="UserCode"/> and open <see cref="VerificationUri"/>.</summary>
public record DeviceCodeInfo(string DeviceCode, string UserCode, string VerificationUri, int Interval, int ExpiresIn);

public interface IGitHubAuthService
{
    /// <summary>Starts the device flow for the given OAuth App Client ID and scope.</summary>
    Task<DeviceCodeInfo> RequestDeviceCodeAsync(string clientId, string scope, CancellationToken ct = default);

    /// <summary>Polls until the user authorizes (returns the access token), is denied, or the code expires (throws).</summary>
    Task<string> PollForTokenAsync(string clientId, string deviceCode, int intervalSeconds, CancellationToken ct = default);

    /// <summary>Returns the authenticated account login for a token (validates it).</summary>
    Task<string> GetLoginAsync(string token, CancellationToken ct = default);
}
