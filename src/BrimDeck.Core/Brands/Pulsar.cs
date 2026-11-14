namespace BrimDeck.Core;

// Pulsar X2 V3 Mini on its Sonix firmware: 3710:5403 its 8K receiver, 3402 on its cable (the X2 CrazyLight Mini on 5406
// and 3414 takes the Compx frame and is read elsewhere). On interface 3, the unnumbered 64-byte feature report: 00 08 81 01
// (the battery query), zeros, and in bytes 62 and 63 the 16-bit sum of the 62 before them, little endian; got back from
// the same report: 00 08 81 01 .. .. <percent>. From HaloBattery's pulsar.py (MIT; after jonkristian/pulsar-x3-python,
// MIT, whose PROTOCOL_ANALYSIS lists the query 08 81 01 with the percent at 6 on the X3; HaloBattery has not had it
// confirmed on an X2 V3 Mini). The collection's usage is not documented: a vendor page is required.
[BrandFamilies]
public static class PulsarMice
{
    public const int Vendor = 0x3710;
    public static readonly int[] Products = [0x5403, 0x3402];

    // Report id 0 first (65 bytes).
    public static byte[] Request()
    {
        var frame = BrandFamilies.Frame(65, 0x00, 0x00, 0x08, 0x81, 0x01);
        int sum = 0;
        for (int i = 1; i < 63; i++) sum += frame[i];
        frame[63] = (byte)sum;
        frame[64] = (byte)(sum >> 8);
        return frame;
    }
    public static BatteryRead? Battery(byte[] r)
        => r.Length >= 8 && r[0] == 0x00 && r[2] == 0x08 && r[3] == 0x81 && r[4] == 0x01 && r[7] is > 0 and <= 100 ? new(r[7]) : null;

    public static bool Takes(HidShape shape) => shape.Vendor == Vendor && Products.Contains(shape.Product) && shape.Interface == 3
        && MouseShapes.VendorPage(shape) && shape.FeatureLength == 65;

    public static IEnumerable<BrandFamily> Families =>
    [
        new("Pulsar", Takes, _ => [new BrandWay("pulsar.sonix", WayTransport.Feature, Request(), Battery) { GapMs = 50, FeatureLength = 65 }])
        { Kind = _ => DeviceKind.Mouse, Prefix = "pulsar-sonix", DisplayName = _ => "Pulsar X2 V3 Mini" },
    ];
}
