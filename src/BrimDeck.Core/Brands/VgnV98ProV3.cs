namespace BrimDeck.Core;

// The VGN V98Pro V3 keyboard over its cable (258A:024E; 0x258A is Sinowealth's id, shared with other products, so only this
// pair). VGN HUB's "beiying" controller asks it on the vendor collection 0xFF02 / 1 with feature report 9, 520 bytes:
// 09 87 00 00 01 00 02 00 and zeros. The same report is read back 25 ms later: 09 87 00 00 01 00 02 00 <percent> <state>,
// with the level at 8 and the high nibble of 9 at 1 while charging. Measured on a V98Pro V3: 100 %, on the cable, returned
// 09 87 00 00 01 00 02 00 64 11. The wireless V98Pro V3 receivers (258A:024F / 027F) are read by VgnV108's 20-byte report
// 0x13 instead; this read is only sent to the wired 024E's 520-byte feature collection.
// Facts from VGN HUB's front end (hub.vgnlab.com), the vendor's own table, and the measurement above.
[BrandFamilies]
public static class VgnV98ProV3
{
    public const int Vendor = 0x258A, Wired = 0x024E, ReportId = 0x09, FeatureLength = 520;
    public const byte Command = 0x87;

    // Feature report 9, the query 87 00 00 01 00 02 00 after the report id, padded to the collection's 520 bytes.
    public static byte[] Request()
    {
        var frame = new byte[FeatureLength];
        frame[0] = ReportId;
        frame[1] = Command;
        frame[4] = 0x01;
        frame[6] = 0x02;
        return frame;
    }

    // The report read back: the query echoed to byte 6, the level at 8, 1 in the high nibble of 9 while charging. A level of
    // 0 is not taken (the request's own echo carries one), nor a level over 100.
    public static BatteryRead? Battery(byte[] r)
        => r.Length >= 10 && r[0] == ReportId && r[1] == Command && r[2] == 0x00 && r[3] == 0x00 && r[4] == 0x01 && r[5] == 0x00
            && r[6] == 0x02 && r[8] is >= 1 and <= 100
            ? new(r[8], (r[9] >> 4 & 0xF) == 1) : null;

    public static IEnumerable<BrandFamily> Families =>
    [
        new("VGN V98Pro V3", shape => shape.Vendor == Vendor && shape.Product == Wired
                && MouseShapes.Collection(shape, 0xFF02, 0x0001) && shape.FeatureLength >= FeatureLength && shape.FeatureIds.Contains(ReportId),
            _ => [new BrandWay("vgn-v98pro-v3-wired.battery", WayTransport.Feature, Request(), Battery)
                { GapMs = 25, FeatureLength = FeatureLength, TimeoutMs = 600 }])
        { Kind = _ => DeviceKind.Keyboard, Prefix = "vgn", DisplayName = _ => "VGN V98Pro V3" },
    ];
}
