namespace BrimDeck.Core;

// BETOP (北通) controllers on their 2.4 GHz receivers, vendor 0x20BC (BETOP's own: the Kunpeng 20 on 20BC:5191 and the KP40D on
// 20BC:515C answer the same status read, so every 20BC receiver is asked it rather than a list of models). The vendor
// collection (usage page 0xFF00, usage 3, interface 1) takes output report 0x02 with command 0x15, zeros after (the report's
// own length); the answer is input report 02 15 <percent> 00 <state>, the state 0x50 on battery and 0x51 charging (its low
// nibble set while charging). A state whose high nibble is not 5 is no answer (the request read back is 02 15 00 00 00).
// From oitsukiii/betop-battery (MIT; Kunpeng 20, checked against BETOP's own client on Windows 11) and HaloBattery's
// beitong.py (MIT; KP40D, from a capture of the official client). Command 0x22 writes the settings and is never sent.
[BrandFamilies]
public static class Betop
{
    public const int Vendor = 0x20BC;

    public static byte[] Request() => BrandFamilies.Frame(2, 0x02, 0x15);
    public static BatteryRead? Battery(byte[] r)
        => r.Length >= 5 && r[0] == 0x02 && r[1] == 0x15 && r[3] == 0x00 && (r[4] & 0xF0) == 0x50 && r[2] <= 100 ? new(r[2], (r[4] & 0x0F) != 0) : null;

    public static IEnumerable<BrandFamily> Families =>
    [
        new("BETOP", shape => shape.Vendor == Vendor && shape.UsagePage >= 0xFF00 && shape.InputLength >= 5,
            _ => [new BrandWay("betop.status", WayTransport.Output, Request(), Battery) { TimeoutMs = 800 }])
        { Kind = _ => DeviceKind.Controller, Prefix = "betop" },
    ];
}
