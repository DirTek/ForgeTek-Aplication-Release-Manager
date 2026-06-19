using System.IO;
using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using ForgeTekUpdatePackager.Models;

namespace ForgeTekUpdatePackager.Services.Publishing;

/// <summary>S3-compatible transport (AWS S3 / Cloudflare R2 / MinIO) via AWSSDK.S3.</summary>
internal sealed class S3Transport : IFileTransport
{
    private readonly string _bucket;
    private readonly string? _prefix;
    private readonly string? _publicBase;
    private readonly string _accessKey;
    private readonly string _secretKey;
    private readonly string? _endpoint;
    private readonly string? _region;

    public S3Transport(AppSettings s)
    {
        _bucket = s.S3Bucket ?? string.Empty;
        _prefix = s.S3Prefix;
        _publicBase = s.S3PublicBaseUrl;
        _accessKey = s.S3AccessKey ?? string.Empty;
        _secretKey = s.S3SecretKey ?? string.Empty;
        _endpoint = s.S3Endpoint;
        _region = s.S3Region;
    }

    public string DisplayName => "S3";

    private AmazonS3Client CreateClient()
    {
        if (string.IsNullOrWhiteSpace(_bucket))
            throw new InvalidOperationException("S3 bucket is not configured.");

        var config = new AmazonS3Config();
        if (!string.IsNullOrWhiteSpace(_endpoint))
        {
            // Custom endpoint (R2 / MinIO / non-AWS) — path-style addressing is the safe default.
            config.ServiceURL = _endpoint;
            config.ForcePathStyle = true;
            if (!string.IsNullOrWhiteSpace(_region)) config.AuthenticationRegion = _region;
        }
        else if (!string.IsNullOrWhiteSpace(_region))
        {
            config.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(_region);
        }

        return new AmazonS3Client(_accessKey, _secretKey, config);
    }

    public async Task<string> TestAsync(CancellationToken ct = default)
    {
        try
        {
            using var client = CreateClient();
            await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = _bucket, MaxKeys = 1 }, ct);
            return $"✓ Reached bucket '{_bucket}'.";
        }
        catch (Exception ex) { return $"✗ {ex.Message}"; }
    }

    public async Task UploadFileAsync(string localPath, string remotePath, IProgress<string> progress, CancellationToken ct = default)
    {
        using var client = CreateClient();
        progress.Report($"  ↑ {remotePath}");
        await client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket,
            Key = remotePath,
            FilePath = localPath,
            DisablePayloadSigning = !string.IsNullOrWhiteSpace(_endpoint), // R2 rejects streaming-signed payloads
        }, ct);
    }

    public async Task UploadTextAsync(string content, string remotePath, CancellationToken ct = default)
    {
        using var client = CreateClient();
        await client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket,
            Key = remotePath,
            ContentBody = content,
            ContentType = "application/json",
            DisablePayloadSigning = !string.IsNullOrWhiteSpace(_endpoint),
        }, ct);
    }

    public async Task<string?> TryDownloadTextAsync(string remotePath, CancellationToken ct = default)
    {
        try
        {
            using var client = CreateClient();
            using var resp = await client.GetObjectAsync(_bucket, remotePath, ct);
            using var reader = new StreamReader(resp.ResponseStream);
            return await reader.ReadToEndAsync(ct);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        catch { return null; }
    }

    public async Task DeleteFileAsync(string remotePath, CancellationToken ct = default)
    {
        using var client = CreateClient();
        await client.DeleteObjectAsync(_bucket, remotePath, ct);
    }

    public async Task DeleteDirAsync(string remoteDir, CancellationToken ct = default)
    {
        using var client = CreateClient();
        var prefix = remoteDir.TrimEnd('/') + "/";
        var list = await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = _bucket, Prefix = prefix }, ct);
        foreach (var obj in list.S3Objects ?? [])
            await client.DeleteObjectAsync(_bucket, obj.Key, ct);
    }

    public string RemotePath(string appKey, string? version, string fileName)
        => PublishPaths.S3Key(_prefix, appKey, version, fileName);

    public string DownloadUrl(string appKey, string version, string fileName)
    {
        var key = PublishPaths.S3Key(_prefix, appKey, version, fileName);
        var baseUrl = string.IsNullOrWhiteSpace(_publicBase)
            ? (string.IsNullOrWhiteSpace(_endpoint) ? $"https://{_bucket}.s3.amazonaws.com" : _endpoint!.TrimEnd('/') + "/" + _bucket)
            : _publicBase;
        return $"{baseUrl!.TrimEnd('/')}/{key}";
    }
}
