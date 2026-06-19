using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace ForgeTekUpdatePackager.Services.Publishing;

/// <summary>HttpContent that streams a file in chunks and reports cumulative bytes sent, so uploads
/// (e.g. a setup .exe to GitHub Releases) can drive a progress bar. Sets Content-Length up front.</summary>
internal sealed class ProgressableStreamContent : HttpContent
{
    private readonly Stream _source;
    private readonly long _length;
    private readonly Action<long>? _onProgress;
    private const int BufferSize = 81920;

    public ProgressableStreamContent(Stream source, long length, string mediaType, Action<long>? onProgress)
    {
        _source = source;
        _length = length;
        _onProgress = onProgress;
        Headers.ContentType = new MediaTypeHeaderValue(mediaType);
    }

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
    {
        var buffer = new byte[BufferSize];
        long sent = 0;
        int read;
        while ((read = await _source.ReadAsync(buffer.AsMemory(0, BufferSize), ct).ConfigureAwait(false)) > 0)
        {
            await stream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            sent += read;
            _onProgress?.Invoke(sent);
        }
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        => SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override bool TryComputeLength(out long length)
    {
        length = _length;
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _source.Dispose();
        base.Dispose(disposing);
    }
}
