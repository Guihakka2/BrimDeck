using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BrimDeck.Core.ClipboardSync;

static class ClipboardSyncTests
{
    public static void Run(Action<string, bool> check)
    {
        // A captured protocol timestamp, independent from this encoder.
        check("WeType timestamp protobuf fixture", WeTypeWire.Parse(Convert.FromHexString("0880d095ffbc31")).GetNumber(1) == 1700000000000);
        byte[] repeated = [9, 8, (byte)'a', (byte)'b', (byte)'c', 22, 3, 0];
        check("Snappy overlapping copies", Encoding.ASCII.GetString(WeTypeWire.Unsnappy(repeated)) == "abcabcabc");
        foreach (byte[] invalid in new byte[][] { [9, 22, 0, 0], [255, 255, 255, 127], [1, 0], [1, 0, 65, 0] })
        {
            bool failed = false;
            try { WeTypeWire.Unsnappy(invalid); } catch (Exception e) when (e is InvalidDataException or OverflowException) { failed = true; }
            check("Reject malformed Snappy " + Convert.ToHexString(invalid), failed);
        }
        foreach (byte[] invalid in new byte[][] { [10, 255, 127], [8, 128], [0], [15], [8, 255, 255, 255, 255, 255, 255, 255, 255, 255, 2] })
        {
            bool failed = false;
            try { WeTypeWire.Parse(invalid); } catch (Exception e) when (e is InvalidDataException or OverflowException) { failed = true; }
            check("Reject malformed protobuf " + Convert.ToHexString(invalid), failed);
        }
        // AES-128 ECB first block from FIPS-197; the protocol also adds a full PKCS7 block.
        var key = Convert.FromHexString("000102030405060708090A0B0C0D0E0F");
        var plain = Convert.FromHexString("00112233445566778899AABBCCDDEEFF");
        var encrypted = WeTypeSyncClient.Crypt(key, plain, true);
        check("AES protocol matches FIPS first block", Convert.ToHexString(encrypted[..16]) == "69C4E0D86A7B0430D8CDB78070B4C55A" && encrypted.Length == 32);
        check("AES protocol removes padding", WeTypeSyncClient.Crypt(key, encrypted, false).SequenceEqual(plain));
        string device = WeTypeIdentity.CreateDeviceCode();
        check("WeType device code checksum", device.Length == 64 && device.StartsWith("AND") && device[32..] == Convert.ToHexString(MD5.HashData(Encoding.ASCII.GetBytes(device[..32] + "aI0wC6qT1nB3nN5r"))));
        check("Sync text uses UTF8 byte count", SyncTransport.TextBytes("测试😀").Length == 10);
        bool oversized = false;
        try { SyncTransport.TextBytes(new string('中', 100000)); } catch (InvalidDataException) { oversized = true; }
        check("Reject oversized Unicode text", oversized);
        // Real response shapes: account registration can succeed while clipboard use is denied.
        using var unavailable = JsonDocument.Parse("""[{"is_current_device":true,"sync_enabled":2,"is_support_copy":false},{"is_current_device":false,"sync_enabled":1,"is_support_copy":true}]""");
        check("Doubao login does not imply sync readiness", !DoubaoDeviceState.ParseCurrent(unavailable.RootElement).CanSync);
        using var ready = JsonDocument.Parse("""[{"is_current_device":true,"sync_enabled":1,"is_support_copy":true}]""");
        check("Doubao supported enabled current device is ready", DoubaoDeviceState.ParseCurrent(ready.RootElement).CanSync);
        foreach (string json in new[] { "[]", "{}", "[{\"is_current_device\":true}]", "[{\"is_current_device\":false,\"sync_enabled\":1,\"is_support_copy\":true}]", "[{\"is_current_device\":true},{\"is_current_device\":true}]" })
        {
            using var data = JsonDocument.Parse(json);
            bool rejected = false;
            try { DoubaoDeviceState.ParseCurrent(data.RootElement); } catch (InvalidDataException) { rejected = true; }
            check("Reject missing or ambiguous Doubao capability " + json, rejected);
        }
        check("Doubao server errors retain only numeric code", new DoubaoRequestException(900102010).Message.Contains("900102010"));
        LanRoutes(check);
        LanFramesAsync(check).GetAwaiter().GetResult();
    }

    private static void LanRoutes(Action<string, bool> check)
    {
        var wifi = new LanInterface("Wi-Fi", IPAddress.Parse("192.168.42.6"), 24);
        var ethernet = new LanInterface("Ethernet", IPAddress.Parse("192.168.12.7"), 24);
        var peer = IPAddress.Parse("192.168.42.19");
        check("LAN selects the sole available host interface", LanNetwork.DefaultHost([wifi]) == wifi);
        check("LAN leaves the host interface unselected when several are available", LanNetwork.DefaultHost([ethernet, wifi]) is null);
        check("LAN joins on the matching interface rather than the first one", LanNetwork.RoutesTo(peer, [ethernet, wifi]).SequenceEqual([wifi.Address]));
        var wide = ethernet with { PrefixLength = 16 };
        var otherMatch = wifi with { Name = "Another adapter", Address = IPAddress.Parse("192.168.42.23") };
        check("LAN prefers the most specific subnet and retains other matching routes",
            LanNetwork.RoutesTo(peer, [wide, wifi, otherMatch]).SequenceEqual([wifi.Address, otherMatch.Address, wide.Address]));
        check("LAN does not connect across disjoint subnets", LanNetwork.RoutesTo(peer, [ethernet]).Count == 0);
        check("LAN reevaluates routes after a local address changes",
            LanNetwork.RoutesTo(peer, [wifi with { Address = IPAddress.Parse("192.168.55.6") }, otherMatch]).SequenceEqual([otherMatch.Address]));
        check("LAN rejects a zero prefix, public IPv4, IPv6 and loopback",
            LanNetwork.Eligible([wifi with { PrefixLength = 0 }, wifi with { Address = IPAddress.Parse("8.8.8.8") },
                wifi with { Address = IPAddress.IPv6Loopback }, wifi with { Address = IPAddress.Loopback }]).Count == 0);
        var narrow = wifi with { Address = IPAddress.Parse("10.0.0.130"), PrefixLength = 25 };
        check("LAN respects non-octet prefix boundaries",
            LanNetwork.IsOnSubnet(IPAddress.Parse("10.0.0.200"), narrow) && !LanNetwork.IsOnSubnet(IPAddress.Parse("10.0.0.100"), narrow));
    }

    private static async Task LanFramesAsync(Action<string, bool> check)
    {
        using var stream = new MemoryStream();
        await LanClipboardLink.KeepAliveAsync(stream, default);
        await LanClipboardLink.WriteTextAsync(stream, "文字\r\n😀", default);
        stream.Position = 0;
        check("LAN keepalive carries no clipboard text", await LanClipboardLink.ReadTextAsync(stream, default) is null);
        check("LAN preserves Unicode and line breaks", await LanClipboardLink.ReadTextAsync(stream, default) == "文字\r\n😀");
        foreach (byte[] invalid in new byte[][] { [0, 4, 0, 1], [255, 255, 255, 255], [0, 0, 0, 1, 255], [0, 0, 0, 2, 65] })
        {
            using var malformed = new MemoryStream(invalid);
            bool rejected = false;
            try { await LanClipboardLink.ReadTextAsync(malformed, default); }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException) { rejected = true; }
            check("LAN rejects oversized, invalid UTF-8 or truncated frame " + Convert.ToHexString(invalid), rejected);
        }
        bool oversized = false;
        try { await LanClipboardLink.WriteTextAsync(stream, new string('中', 100000), default); }
        catch (InvalidDataException) { oversized = true; }
        check("LAN refuses oversized outbound text", oversized);

        bool invalidCode = false;
        try { await LanClipboardLink.JoinAsync("BD-LAN-1.invalid", default); }
        catch (FormatException) { invalidCode = true; }
        check("LAN rejects malformed invitation before connecting", invalidCode);
    }
}
