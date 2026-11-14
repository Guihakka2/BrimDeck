namespace BrimDeck.Core;

// Flydigi (飞智) controllers on their dongles and cables, vendor 0x37D7 (Flydigi's own: Vader 5 Pro 2401, Apex 5 2501, Apex 6
// 2502, and whatever comes next is asked the same). The pad keeps its own protocol on the vendor collection usage page 0xFFA0,
// usage 1 (as SDL's hidapi layer and HaloBattery read it on Windows; 0xA0FF is taken too, in case Windows reports the page
// byte-swapped). The read is the device info request 5A A5 01 02 00, sent as output report 0x03 where the collection numbers
// its reports (Apex 5) and as unnumbered report 0x00 where it does not (Vader 5, Apex 6: SDL zeroes the id for them). The
// answer is an input report 5A A5 01 ..., the report id first where there is one; byte 5 of it (counted from 5A) is the
// device id, byte 11 the battery: high nibble 0 on battery, 1 charging, 2 charged, low nibble the level step. The step is
// × 20 (0 to 5) on every model but the Apex 6, where SDL takes it × 10 (0 to 10): SDL_hidapi_flydigi.c (zlib,
// HandleInfoResponse) decides by the device id (149, 150, 152 are the Apex 6) and HaloBattery's flydigi.py (MIT, checked on
// a Vader 5 Pro) agrees on × 20 for the rest; a seven-step reading elsewhere has no source code behind it, so it is not
// followed. Coarse either way. SDL's acquire command (0x1C) takes the pad over and is never sent; neither is anything else.
[BrandFamilies]
public static class Flydigi
{
    public const int Vendor = 0x37D7;
    public const int Apex6 = 0x2502;
    private static readonly byte[] Info = [0x5A, 0xA5, 0x01, 0x02, 0x00];
    private static readonly HashSet<int> Apex6Ids = [149, 150, 152];

    public static bool Collection(HidShape shape) => shape.Vendor == Vendor && shape.UsagePage is 0xFFA0 or 0xA0FF && shape.Usage == 0x0001
        && shape.InputLength >= 32;

    // The request as the collection takes it: report 0x03 where it declares that id, else unnumbered.
    public static byte[] Request(bool numbered) => [numbered ? (byte)0x03 : (byte)0x00, .. Info];

    public static BatteryRead? Battery(byte[] r, bool apex6)
    {
        int at = r.Length > 0 && r[0] == 0x5A ? 0 : 1;
        if (r.Length - at < 31 || r[at] != 0x5A || r[at + 1] != 0xA5 || r[at + 2] != 0x01) return null;
        // The request read back carries no device id (its byte 5 is padding).
        if (r[at + 5] == 0) return null;
        bool tens = apex6 || Apex6Ids.Contains(r[at + 5]);
        int state = r[at + 11] >> 4, step = r[at + 11] & 0x0F;
        if (state > 2 || step > (tens ? 10 : 5)) return null;
        return new(state == 2 ? 100 : step * (tens ? 10 : 20), state is 1 or 2, Coarse: true);
    }

    // Windows.Gaming.Input's report for a Flydigi pad is the constant remaining = full = 1000 mWh whatever the battery holds
    // (HaloBattery #191: it said full for a Vader 4 Pro flashing low, and the same for a Vader 5 Pro on its dongle). The Vader 4
    // Pro shows up under Microsoft's Xbox 360 id 045E:028E with Flydigi's name. Such a report is no reading; another vendor's
    // 1000 of 1000 can be a full battery and is kept.
    public static bool WindowsPlaceholder(int vendor, int product, string? name, int? remaining, int? full)
        => remaining == 1000 && full == 1000 && (vendor == Vendor || vendor == 0x045E && product == 0x028E
            && name is { } text && (text.Contains("flydigi", StringComparison.OrdinalIgnoreCase) || text.Contains("vader", StringComparison.OrdinalIgnoreCase)));

    public static IEnumerable<BrandFamily> Families =>
    [
        new("Flydigi", Collection, shape =>
        {
            bool numbered = shape.OutputIds.Contains(0x03);
            bool apex6 = shape.Product == Apex6;
            return [new BrandWay("flydigi.info", WayTransport.Output, Request(numbered), r => Battery(r, apex6)) { TimeoutMs = 600 }];
        })
        { Kind = _ => DeviceKind.Controller, Prefix = "flydigi" },
    ];
}
