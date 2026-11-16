using System.Net.Http;
using System.Text;

namespace BrimDeck.Core.ClipboardSync;

public sealed record RemoteClip(string Id, string Text);

public interface IClipboardSyncClient : IDisposable
{
    Task ConnectAsync(CancellationToken cancellation);
    Task SendAsync(string text, CancellationToken cancellation);
    Task<RemoteClip?> ReceiveAsync(CancellationToken cancellation);
}

public static class SyncTransport
{
    public const int MaxTextBytes = 256 * 1024;
    public static readonly UTF8Encoding Utf8 = new(false, true);
    public static HttpClient CreateHttp() => new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(20) };
    public static byte[] TextBytes(string text)
    {
        var bytes = Utf8.GetBytes(text);
        if (bytes.Length is 0 or > MaxTextBytes) throw new InvalidDataException("Text must contain 1–262144 UTF-8 bytes.");
        return bytes;
    }
    public static async Task<byte[]> ReadAsync(HttpResponseMessage response, CancellationToken ct, int limit = 2 * 1024 * 1024)
    {
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Response is too large.");
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + count > limit) throw new InvalidDataException("Response is too large.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
}
