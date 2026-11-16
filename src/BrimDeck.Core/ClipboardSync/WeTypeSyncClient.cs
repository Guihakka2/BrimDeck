using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Asn1.Sec;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Utilities;

namespace BrimDeck.Core.ClipboardSync;

public sealed class WeTypeIdentity
{
    public string DeviceName { get; set; } = "BrimDeck " + Environment.MachineName;
    public string DisplayName { get; set; } = "";
    public string PrivateKey { get; set; } = "";
    public string DeviceCode { get; set; } = CreateDeviceCode();
    public string Uuid { get; set; } = Guid.NewGuid().ToString();
    public string ServerKey { get; set; } = "";
    public ulong Uin { get; set; }
    public bool Paired { get; set; }
    public static string CreateDeviceCode()
    {
        string prefix = "AND" + "BrimDeck;Windows;standalone"[..17] + Guid.NewGuid().ToString("N")[..12];
        return prefix + Convert.ToHexString(MD5.HashData(Encoding.ASCII.GetBytes(prefix + "aI0wC6qT1nB3nN5r")));
    }
}

// Independently implemented control client. Wire fields and protocol constants were checked
// against mkdir700/wx-ime-sdk; no external executable or SDK is loaded.
public sealed class WeTypeSyncClient(WeTypeIdentity identity, Action save) : IClipboardSyncClient
{
    private const string Version = "3.5.4.57423";
    private string Name => identity.DeviceName;
    private readonly HttpClient _http = SyncTransport.CreateHttp();
    private byte[]? _key;
    private long _offset;
    private uint _task = 1;
    private ulong _group;
    private HashSet<ulong> _members = [];
    private static readonly ECDomainParameters Domain = new(SecNamedCurves.GetByName("secp128r1"));
    private string DeviceInfo => "BrimDeck;Windows;standalone;BrimDeck;sdk 34;PHONE";
    private long Stamp => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + _offset;

    public static byte[] Crypt(byte[] key, byte[] bytes, bool encrypt)
    {
        using var aes = Aes.Create(); aes.Key = key;
        return encrypt ? aes.EncryptEcb(bytes, PaddingMode.PKCS7) : aes.DecryptEcb(bytes, PaddingMode.PKCS7);
    }
    private static void Success(WeTypeWire value, int field = 1)
    {
        if (!value.Has(field) || value.GetNumber(field) != 0) throw new InvalidDataException("WeType rejected the request (" + value.GetNumber(field) + ").");
    }
    public async Task ConnectAsync(CancellationToken cancellation)
    {
        await InitializeAsync(cancellation);
        await GroupAsync(cancellation);
    }
    private async Task InitializeAsync(CancellationToken ct)
    {
        long start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var clock = await PacketAsync("/timestamp", 0, [], null, 0, "", ct);
        ulong serverTime = clock.GetNumber(1);
        if (serverTime == 0 || serverTime > long.MaxValue) throw new InvalidDataException("Missing server time.");
        _offset = (long)serverTime - (start + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) / 2;
        if (identity.PrivateKey.Length == 0)
        {
            identity.PrivateKey = Convert.ToHexString(BigIntegers.AsUnsignedByteArray(16, BigIntegers.CreateRandomInRange(BigInteger.One, Domain.N.Subtract(BigInteger.One), new SecureRandom())));
            save();
        }
        var scalar = new BigInteger(1, Convert.FromHexString(identity.PrivateKey));
        if (scalar.SignValue <= 0 || scalar.CompareTo(Domain.N) >= 0) throw new InvalidDataException("Invalid local identity.");
        var publicKey = Convert.ToHexString(Domain.G.Multiply(scalar).Normalize().GetEncoded(false));
        var body = new WeTypeWire().Text(1, publicKey).Text(2, identity.ServerKey).Number(3, identity.Uin == 0 ? 1UL : 0)
            .Text(4, identity.DeviceCode).Text(7, "").Text(8, DeviceInfo).Text(9, Name);
        if (identity.Uin == 0) body.Text(6, ""); else body.Number(5, identity.Uin);
        var handshake = await PacketAsync("/oauth_pubkey_v2", 0, body.Encode(), Encoding.ASCII.GetBytes("G7E1C8B0D7D7E4L3J2U9D6W0B2M8Q6O5"), 2147483646, "", ct);
        Success(handshake);
        var pointBytes = Convert.FromHexString(handshake.GetText(2));
        if (pointBytes.Length != 33 || pointBytes[0] != 4) throw new InvalidDataException("Invalid peer public key.");
        var point = Domain.Curve.DecodePoint(pointBytes);
        if (point.IsInfinity || !point.IsValid()) throw new InvalidDataException("Invalid peer point.");
        var agreement = new ECDHBasicAgreement(); agreement.Init(new ECPrivateKeyParameters(scalar, Domain));
        byte[] key = Encoding.ASCII.GetBytes(Convert.ToHexString(BigIntegers.AsUnsignedByteArray(16, agreement.CalculateAgreement(new ECPublicKeyParameters(point, Domain)))));
        if (identity.Uin == 0)
        {
            if (handshake.GetText(4).Length == 0) throw new InvalidDataException("Missing registration token.");
            var registration = await PacketAsync("/gen_uin_v2", 0,
                new WeTypeWire().Text(1, identity.DeviceCode).Text(2, handshake.GetText(2)).Text(4, identity.Uuid).Text(5, "").Encode(), key, 2147483645, handshake.GetText(4), ct);
            Success(registration); identity.Uin = registration.GetNumber(2);
            if (identity.Uin == 0) throw new InvalidDataException("Missing device identifier.");
        }
        identity.ServerKey = handshake.GetText(2); _key = key; save();
    }
    public async Task PairAsync(string code, CancellationToken ct)
    {
        if (code.Length != 6 || code.Any(c => c is < '0' or > '9')) throw new ArgumentException("A six-digit pairing code is required.");
        await InitializeAsync(ct);
        var joined = await ApiAsync(31, new WeTypeWire().Number(1, identity.Uin).Number(2, 1).Text(3, code)
            .Message(5, new WeTypeWire().Text(1, Name).Number(2, 1)), ct);
        Success(joined);
        string bind = joined.GetText(2), peerName = joined.GetMessage(4).GetText(1);
        if (bind.Length == 0 || peerName.Length == 0) throw new InvalidDataException("Missing pairing transaction.");
        try
        {
            for (int attempt = 0; attempt < 30; attempt++)
            {
                var result = await ApiAsync(38, new WeTypeWire().Number(1, identity.Uin).Number(2, 1).Text(3, bind), ct);
                Success(result);
                if (!result.Has(2)) throw new InvalidDataException("Missing pairing status.");
                if (result.GetNumber(2) == 1)
                {
                    var group = await GroupAsync(ct);
                    if (!group.Messages(2).Any(d => d.GetNumber(1) != identity.Uin && d.GetMessage(2).GetText(1) == peerName)) throw new InvalidDataException("Pairing peer mismatch.");
                    identity.Paired = true; save(); return;
                }
                if (result.GetNumber(2) != 0) throw new InvalidDataException("Pairing was declined or expired.");
                await Task.Delay(1500, ct);
            }
            throw new TimeoutException("Pairing confirmation timed out.");
        }
        catch
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await ApiAsync(33, new WeTypeWire().Number(1, identity.Uin).Number(2, 1).Text(4, bind), cleanup.Token); } catch { }
            throw;
        }
    }
    public async Task<string> CreatePairingCodeAsync(CancellationToken ct)
    {
        await InitializeAsync(ct);
        var result = await ApiAsync(30, new WeTypeWire().Number(1, identity.Uin).Number(2, 1)
            .Message(3, new WeTypeWire().Text(1, Name).Number(2, 1)), ct);
        Success(result);
        string code = result.GetText(2);
        if (code.Length != 6 || code.Any(c => c is < '0' or > '9')) throw new InvalidDataException("Invalid pairing code.");
        return code;
    }
    public async Task<(string BindId, string Name)?> PollPairingCodeAsync(string code, CancellationToken ct)
    {
        var result = await ApiAsync(40, new WeTypeWire().Number(1, identity.Uin).Number(2, 1).Text(3, code), ct);
        if (!result.Has(1)) throw new InvalidDataException("Missing pairing state.");
        if (result.GetNumber(1) == 0) return null;
        if (result.GetNumber(1) != 1) throw new InvalidDataException("Pairing code expired.");
        string bind = result.GetText(2), name = result.GetMessage(4).GetText(1);
        if (bind.Length == 0 || name.Length == 0) throw new InvalidDataException("Missing pairing peer.");
        return (bind, name);
    }
    public async Task ConfirmPairingAsync(string bind, string name, CancellationToken ct)
    {
        var result = await ApiAsync(32, new WeTypeWire().Number(1, identity.Uin).Number(2, 1).Text(3, bind), ct);
        Success(result);
        ulong peer = result.GetMessage(3).GetNumber(1);
        var group = await GroupAsync(ct);
        if (peer == 0 || peer == identity.Uin || result.GetNumber(2) != _group ||
            !group.Messages(2).Any(d => d.GetNumber(1) == peer && d.GetMessage(2).GetText(1) == name))
            throw new InvalidDataException("Pairing peer mismatch.");
        identity.Paired = true; save();
    }
    public async Task ReleasePairingCodeAsync(string code, CancellationToken ct) =>
        _ = await ApiAsync(33, new WeTypeWire().Number(1, identity.Uin).Number(2, 1).Text(3, code), ct);
    private async Task<WeTypeWire> GroupAsync(CancellationToken ct, bool clipboard = false)
    {
        var request = new WeTypeWire().Number(1, identity.Uin).Number(2, 1).Number(3, clipboard ? 3UL : 1UL);
        if (clipboard) request.Number(4, 0);
        var response = await ApiAsync(34, request, ct); Success(response, 2);
        var group = response.GetMessage(1);
        string display = group.Messages(2).FirstOrDefault(d => d.GetNumber(1) == identity.Uin)?.GetMessage(2).GetText(1) ?? "";
        if (display.Length > 0 && identity.DisplayName != display) { identity.DisplayName = display; save(); }
        _group = group.GetNumber(1); _members = group.Messages(2).Select(d => d.GetNumber(1)).ToHashSet();
        if (_group == 0 || !_members.Contains(identity.Uin) || _members.Count < 2) throw new InvalidDataException("WeType is not paired.");
        return group;
    }
    public async Task<(string Name, string OriginalName, string Remark)> ReadOwnNamesAsync(CancellationToken ct)
    {
        await InitializeAsync(ct);
        var response = await ApiAsync(34, new WeTypeWire().Number(1, identity.Uin).Number(2, 1).Number(3, 1), ct);
        Success(response, 2);
        var own = response.GetMessage(1).Messages(2).FirstOrDefault(d => d.GetNumber(1) == identity.Uin)
            ?? throw new InvalidDataException("Own device missing.");
        return (own.GetMessage(2).GetText(1), own.GetMessage(2).GetText(6), response.GetText(3));
    }
    public async Task SendAsync(string text, CancellationToken ct)
    {
        var bytes = SyncTransport.TextBytes(text);
        await GroupAsync(ct);
        var response = await ApiAsync(36, new WeTypeWire().Number(1, identity.Uin).Number(2, 1).Number(4, _group)
            .Message(3, new WeTypeWire().Number(2, 1).Bytes(6, bytes).Text(9, Guid.NewGuid().ToString("N"))), ct);
        Success(response);
        if (response.GetNumber(2) == 0) throw new InvalidDataException("Missing send receipt.");
    }
    public async Task<RemoteClip?> ReceiveAsync(CancellationToken ct)
    {
        var group = await GroupAsync(ct, true); var clip = group.GetMessage(3);
        ulong sender = clip.GetNumber(7), version = clip.GetNumber(1), expiry = clip.GetNumber(4);
        if (version == 0 || sender == identity.Uin || expiry != 0 && expiry <= (ulong)(Stamp / 1000)) return null;
        if (!_members.Contains(sender)) throw new InvalidDataException("Unknown clipboard sender.");
        if (clip.GetNumber(2) != 1) return null; // Text only; images and remote objects are not downloaded.
        var bytes = clip.GetBytes(6);
        if (bytes.Length is 0 or > SyncTransport.MaxTextBytes) return null;
        return new RemoteClip($"{_group}:{version}", SyncTransport.Utf8.GetString(bytes));
    }
    private async Task<WeTypeWire> ApiAsync(uint command, WeTypeWire body, CancellationToken ct)
    {
        if (_key is null) throw new InvalidOperationException("Connect first.");
        var headers = new WeTypeWire();
        foreach (var pair in new[] { ("vid", identity.Uin.ToString()), ("app_ver", Version), ("platform", "1"), ("device", DeviceInfo), ("channelid", ""), ("client_timestamp", Stamp.ToString()) })
            headers.Message(1, new WeTypeWire().Text(1, pair.Item1).Text(2, pair.Item2));
        var envelope = new WeTypeWire().Number(2, identity.Uin).Number(3, command).Message(4, headers).Message(5, body);
        var response = await PacketAsync("/api_v2", command, envelope.Encode(), _key, _task++, "", ct);
        if (!response.Has(6)) throw new InvalidDataException("Missing response body.");
        return response.GetMessage(6);
    }
    private async Task<WeTypeWire> PacketAsync(string path, uint command, byte[] plain, byte[]? key, uint task, string token, CancellationToken ct)
    {
        var bytes = key is null ? plain : Crypt(key, plain, true);
        var headers = new Dictionary<string, string> {
            ["Kb-OsType"]="1", ["Kb-Version"]=Version, ["Kb-SystemVersion"]="34", ["Kb-PackageType"]="2", ["Kb-TimeStamp"]=Stamp.ToString(),
            ["Kb-BodyMd5"]=Convert.ToHexString(MD5.HashData(bytes)), ["Kb-TraceId"]=Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant(),
            ["Kb-TaskId"]=task.ToString(), ["Kb-Scene"]="1", ["Kb-CompressionType"]="1", ["Use_DebugNet"]="0" };
        var order = new List<string> { "Kb-OsType", "Kb-Version", "Kb-Scene" };
        if (path == "/oauth_pubkey_v2") { headers["Kb-Uin"] = identity.Uin.ToString(); headers["Kb-DeviceCodeRestrictionV2"] = "1"; }
        if (path == "/gen_uin_v2") { headers["Kb-GenUinToken"] = token; headers["Kb-DeviceCodeRestrictionV2"] = "1"; }
        if (path == "/api_v2")
        {
            headers["Kb-Uin"] = identity.Uin.ToString(); headers["Kb-CmdId"] = "4814"; headers["Kb-SubCmdId"] = command.ToString();
            headers["Kb-SharedKeySuffix"] = Encoding.ASCII.GetString(key![28..]); order.AddRange(["Kb-CmdId", "Kb-SubCmdId"]);
        }
        order.AddRange(["Kb-TimeStamp", "Kb-BodyMd5"]);
        if (path == "/gen_uin_v2") order.Add("Kb-GenUinToken");
        if (path == "/api_v2") order.Add("Kb-Uin");
        order.AddRange(["Kb-TraceId", "Kb-TaskId"]);
        if (path != "/timestamp") headers["Kb-Sign"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(order.Select(k => headers[k])) + "aI0wC6qT1nB3nN5r")));
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://wetype.weixin.qq.com" + path) { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new("application/octet-stream");
        foreach (var pair in headers) request.Headers.Add(pair.Key, pair.Value);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        bytes = await SyncTransport.ReadAsync(response, ct);
        if (key is not null) bytes = Crypt(key, bytes, false);
        var compression = response.Headers.TryGetValues("Kb-CompressionType", out var values) ? values.FirstOrDefault() : "1";
        if (compression == "2") bytes = WeTypeWire.Unsnappy(bytes);
        else if (compression is not ("1" or null or "")) throw new InvalidDataException("Unsupported packet compression.");
        return WeTypeWire.Parse(bytes);
    }
    public void Dispose() { _http.Dispose(); if (_key is not null) CryptographicOperations.ZeroMemory(_key); }
}
