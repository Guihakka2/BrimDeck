using System.Net.Http;
using System.Text;
using System.Text.Json;
using ZstdSharp;

namespace BrimDeck.Core.ClipboardSync;

public sealed class DoubaoIdentity
{
    public string DeviceName { get; set; } = "BrimDeck " + Environment.MachineName;
    public string DeviceId { get; set; } = "";
    public Dictionary<string, string> Cookies { get; set; } = [];
}

public sealed class DoubaoDeviceUnavailableException(int syncState, bool supportsCopy) : Exception("The current Doubao device is not ready for clipboard sync.")
{
    public int SyncState { get; } = syncState;
    public bool SupportsCopy { get; } = supportsCopy;
}

public sealed class DoubaoRequestException(int code) : Exception($"Doubao clipboard request failed (code {code}).")
{
    public int Code { get; } = code;
}

public sealed record DoubaoDeviceState(int SyncState, bool SupportsCopy)
{
    public bool CanSync => SyncState == 1 && SupportsCopy;

    public static DoubaoDeviceState ParseCurrent(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Invalid device list.");
        var current = data.EnumerateArray().Where(d => d.ValueKind == JsonValueKind.Object && d.TryGetProperty("is_current_device", out var own) && own.ValueKind == JsonValueKind.True).ToArray();
        if (current.Length != 1) throw new InvalidDataException("Missing or ambiguous current Doubao device.");
        if (!current[0].TryGetProperty("sync_enabled", out var state) || state.ValueKind != JsonValueKind.Number || !state.TryGetInt32(out int syncState) ||
            !current[0].TryGetProperty("is_support_copy", out var supported) || supported.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException("Missing current Doubao device capabilities.");
        return new(syncState, supported.GetBoolean());
    }
}

public sealed class DoubaoSyncClient(DoubaoIdentity identity) : IClipboardSyncClient
{
    private readonly HttpClient _http = SyncTransport.CreateHttp();
    public async Task ConnectAsync(CancellationToken cancellation)
    {
        var state = await ReadCurrentDeviceAsync(cancellation);
        if (!state.CanSync) throw new DoubaoDeviceUnavailableException(state.SyncState, state.SupportsCopy);
    }
    public async Task<DoubaoDeviceState> ReadCurrentDeviceAsync(CancellationToken cancellation)
    {
        if (identity.DeviceId.Length == 0 || !identity.Cookies.ContainsKey("sessionid")) throw new InvalidDataException("Sign in to Doubao first.");
        var data = await RequestAsync("/api/v2/clipboard/config", null, null, cancellation);
        return DoubaoDeviceState.ParseCurrent(data);
    }
    public async Task SendAsync(string text, CancellationToken cancellation)
    {
        var bytes = SyncTransport.TextBytes(text);
        using var compressor = new Compressor(3);
        var data = await RequestAsync("/api/v2/stream/clipboard/text/copy", new() { ["text_length"] = bytes.Length.ToString() }, compressor.Wrap(bytes).ToArray(), cancellation);
        if (!data.TryGetProperty("version_seq", out var version) || version.GetInt64() <= 0) throw new InvalidDataException("Missing send receipt.");
    }
    public async Task<RemoteClip?> ReceiveAsync(CancellationToken cancellation)
    {
        var data = await RequestAsync("/api/v2/clipboard/pull", new() { ["start_version_seq"] = "0" }, null, cancellation);
        if (!data.TryGetProperty("has_content", out var has) || !has.GetBoolean()) return null;
        if (!data.TryGetProperty("from_device_id", out var sender) || sender.ToString() == identity.DeviceId) return null;
        if (!data.TryGetProperty("plain_text_payload", out var payload) || payload.ValueKind != JsonValueKind.Object) return null;
        if (payload.TryGetProperty("is_tos_load", out var external) && external.GetBoolean()) return null;
        if (!payload.TryGetProperty("plain_text", out var text) || text.ValueKind != JsonValueKind.String) return null;
        string value = text.GetString()!;
        if (value.Length > 2 * SyncTransport.MaxTextBytes) throw new InvalidDataException("Text payload too large.");
        var bytes = Convert.FromBase64String(value);
        using var input = new MemoryStream(bytes);
        using var decoder = new DecompressionStream(input);
        using var output = new MemoryStream();
        var buffer = new byte[8192]; int count;
        while ((count = await decoder.ReadAsync(buffer, cancellation)) > 0)
        {
            if (output.Length + count > SyncTransport.MaxTextBytes) throw new InvalidDataException("Decoded text too large.");
            output.Write(buffer, 0, count);
        }
        return new RemoteClip(data.GetProperty("version_seq").ToString(), SyncTransport.Utf8.GetString(output.ToArray()));
    }
    private async Task<JsonElement> RequestAsync(string path, Dictionary<string, string>? extra, byte[]? body, CancellationToken ct)
    {
        var query = new Dictionary<string, string> { ["device_name"] = identity.DeviceName, ["device_id"] = identity.DeviceId, ["x_device_type"] = "3", ["aid"] = "685343", ["device_platform"] = "windows", ["device_type"] = "windows", ["version_name"] = "1.0.0", ["version_code"] = "10000" };
        if (extra is not null) foreach (var pair in extra) query[pair.Key] = pair.Value;
        string url = "https://ime.doubao.com" + path + "?" + string.Join("&", query.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, url);
        request.Headers.Add("Cookie", string.Join("; ", identity.Cookies.Where(p => p.Key is "sessionid" or "sessionid_ss" or "passport_csrf_token" or "passport_csrf_token_default").Select(p => p.Key + "=" + p.Value)));
        request.Headers.Add("X-Device-Type", "3");
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body); request.Content.Headers.ContentType = new("application/octet-stream");
            string now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
            request.Headers.Add("copy-clipboard-updated-time", now); request.Headers.Add("client-copy-req-time", now);
        }
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var raw = await SyncTransport.ReadAsync(response, ct);
        using var document = JsonDocument.Parse(raw);
        if (!document.RootElement.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.Number || !code.TryGetInt32(out int value))
            throw new InvalidDataException("Missing Doubao response code.");
        if (value != 0) throw new DoubaoRequestException(value);
        return document.RootElement.GetProperty("data").Clone();
    }
    public void Dispose() => _http.Dispose();
}
