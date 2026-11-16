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
    }
}
