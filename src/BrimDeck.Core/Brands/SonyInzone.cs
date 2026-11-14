namespace BrimDeck.Core;

// Sony's INZONE headsets on their 2.4 GHz dongles, vendor 0x054C (shared with the PlayStation controllers and the PULSE 3D,
// which other readers take), so only these products:
// - INZONE H5 (0EBF, usage page 0xFF04 usage 2) and H9 II (0FA8, usage page 0xFF04 usage 1, interface 5): Sony's vendor HCI
//   over HID on report 02. The battery GET is 02 0C 01 00 FC 08 96 C3 41 04 01 <transaction, little endian> <sum of bytes 6
//   to 12>; the transaction is 2 (the dongle's own notices carry 1). The answer is an HCI event: 02 <length> 04 FF <param
//   length> 00 96 C3 <address, high nibble 1 = to the PC> 04 <10 = RET> <transaction> <charger> <percent>, the sum of bytes 5
//   to <length> at <length> + 1; the percent 0xFF is the headset offline. HeadsetControl's sony_inzone_protocol.hpp (GPL;
//   facts only). No answer from a device is quoted there; the H5 and H9 II are listed as supported.
// - INZONE Buds (0EC2, usage page 0xFF04 usage 1): the dongle sends report 02 12 04 on its own, the right bud's percent at
//   14, the left's at 16, the case's at 18, and (14 + 16 + 117) mod 256 at 19; nothing is sent to it. The lower of the two
//   buds is the reading (as HeadsetControl's sony_inzone_buds.hpp reports it; GPL, facts only); the case is not given.
// Not read: the INZONE H9 (0E53) speaks the same commands over a serial port. The PULSE Explore is SonyPulse.
[BrandFamilies]
public static class SonyInzone
{
    public const int Vendor = 0x054C, H5 = 0x0EBF, H9II = 0x0FA8, Buds = 0x0EC2;
    private const byte Transaction = 0x02;

    public static byte[] Request()
    {
        var frame = BrandFamilies.Frame(14, 0x02, 0x0C, 0x01, 0x00, 0xFC, 0x08, 0x96, 0xC3, 0x41, 0x04, 0x01, Transaction, 0x00);
        int sum = 0;
        for (int i = 6; i <= 12; i++) sum += frame[i];
        frame[13] = (byte)sum;
        return frame;
    }

    public static BatteryRead? Battery(byte[] r)
    {
        if (r.Length < 15 || r[0] != 0x02 || r[2] != 0x04 || r[3] != 0xFF || r[5] != 0x00 || r[6] != 0x96 || r[7] != 0xC3 || r[8] >> 4 != 0x1) return null;
        int length = r[1];
        if (length < 14 || length + 1 >= r.Length) return null;
        int sum = 0;
        for (int i = 5; i <= length; i++) sum += r[i];
        if ((byte)sum != r[length + 1]) return null;
        if (r[9] != 0x04 || r[10] is not (0x10 or 0xA0) || r[11] != Transaction || r[12] != 0x00) return null;
        if (r[14] == 0xFF) return new(null, Asleep: true);
        return r[14] is > 0 and <= 100 ? new(r[14], r[13] != 0) : null;
    }

    public static BatteryRead? BudsBattery(byte[] r)
    {
        if (r.Length < 20 || r[0] != 0x02 || r[1] != 0x12 || r[2] != 0x04 || (byte)(r[14] + r[16] + 117) != r[19]) return null;
        int right = r[14], left = r[16];
        if (right > 100 || left > 100) return null;
        int low = Math.Min(left, right);
        return low > 0 ? new(low) : null;
    }

    private static bool Takes(HidShape s) => s.Vendor == Vendor && HeadsetPages.Is(s, 0xFF04) && s.Product switch
    {
        H5 => s.Usage == 0x0002 && s.OutputLength >= 64,
        H9II => s.Usage == 0x0001 && s.Interface is 5 or -1 && s.OutputLength >= 64,
        Buds => s.Usage == 0x0001 && s.InputLength >= 20,
        _ => false
    };

    private static IReadOnlyList<BrandWay> Ways(HidShape s) => s.Product == Buds
        ? [new BrandWay("inzone.buds", WayTransport.Listen, [0x02], BudsBattery) { TimeoutMs = 1000 }]
        : [new BrandWay("inzone.battery", WayTransport.Output, Request(), Battery) { TimeoutMs = 1000 }];

    public static IEnumerable<BrandFamily> Families =>
    [
        new("Sony INZONE", Takes, Ways) { Kind = s => s.Product == Buds ? DeviceKind.Earbuds : DeviceKind.Headphones, Prefix = "inzone" },
    ];
}
