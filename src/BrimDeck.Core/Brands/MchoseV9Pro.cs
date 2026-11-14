namespace BrimDeck.Core;

// MCHOSE V9 Pro headset on its C-Media receiver or cable (291D:385D): output report 55 65 01, zeros to 64 bytes, on a vendor
// collection, answered 55 65 <percent> <state>, the state 0x00 on its cable (charging) and 0x02 on battery as measured on
// the reporter's headset (HaloBattery's mchose_v9.py, MIT, after JoaoKSS/MCHOSE_v9_PRO_Controller; the level followed M HUB
// while charging). 291D is C-Media's id, which MCHOSE's S9 PRO, G9 PRO and V9 share (wzk112/MchoseHub's catalog), so only a
// collection whose product string names the V9 PRO is asked. A receiver firmware below 0008 does not answer at all.
[BrandFamilies]
public static class MchoseV9Pro
{
    public const int Vendor = 0x291D, Product = 0x385D;

    public static byte[] Request() => BrandFamilies.Frame(64, 0x55, 0x65, 0x01);
    public static BatteryRead? Battery(byte[] r)
        => r.Length >= 4 && r[0] == 0x55 && r[1] == 0x65 && r[2] <= 100 ? new(r[2], r[3] == 0x00) : null;

    public static IEnumerable<BrandFamily> Families =>
    [
        new("MCHOSE V9 Pro", shape => shape.Vendor == Vendor && shape.Product == Product && UsagePages.Vendor(shape.UsagePage)
                && shape.OutputLength >= 64 && shape.Name.Contains("V9 PRO", StringComparison.OrdinalIgnoreCase),
            _ => [new BrandWay("mchose.v9pro", WayTransport.Output, Request(), Battery) { TimeoutMs = 500 }])
        { Kind = _ => DeviceKind.Headphones, Prefix = "headset" },
    ];
}
