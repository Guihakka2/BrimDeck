namespace BrimDeck.Core;

// Ryunix Kyu Pro MX1 and the Amkette XS series, one OEM firmware on Elan's shared vendor id 0x04F3, so only its products:
// 026F the receiver, 026E the cable (026D is the XS's Bluetooth id, whose collections are not opened). Nothing is sent:
// the configuration collection (usage page 0x0A, usage 0xC7 on the MX1 and 0 on the XS) carries input report 0x04 with the
// mouse's status, 04 <active 0/1> <stage> <polling code> <percent> <battery status> ..., the status 0 discharging, 1
// charging, 2 full on the cable. A polling code outside the firmware's table, a status above 2 or a percent above 100 is
// another report. From OpenMouse's Ryunix and Amkette modules (AGPL; facts only: a capture on a wired XS Flow 2 Speed,
// 04 01 02 01 64 02 00 00 01 20, 100 % full, and the MX1's contributed reports).
[BrandFamilies]
public static class RyunixMice
{
    public const int Vendor = 0x04F3;
    public static readonly int[] Products = [0x026E, 0x026F];
    private static readonly int[] PollingCodes = [0x08, 0x04, 0x02, 0x01, 0x11, 0x12, 0x14];

    public static BatteryRead? Battery(byte[] r)
        => r.Length >= 7 && r[0] == 0x04 && r[1] <= 1 && PollingCodes.Contains(r[3]) && r[5] <= 2 && r[4] is > 0 and <= 100
            ? new(r[4], r[5] == 1) : null;

    public static bool Takes(HidShape shape) => shape.Vendor == Vendor && Products.Contains(shape.Product)
        && (MouseShapes.Collection(shape, 0x000A, 0xC7) || MouseShapes.Collection(shape, 0x000A, 0x00)) && shape.InputLength >= 7;

    public static IEnumerable<BrandFamily> Families =>
    [
        new("Ryunix", Takes, _ => [new BrandWay("ryunix.listen", WayTransport.Listen, [], Battery) { TimeoutMs = 600, Attempts = 1 }])
        { Kind = _ => DeviceKind.Mouse, Prefix = "ryunix" },
    ];
}
