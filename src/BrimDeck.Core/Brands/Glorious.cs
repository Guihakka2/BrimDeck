namespace BrimDeck.Core;

// Glorious wireless mice on Sinowealth's shared vendor id 0x258A, so only these products: the Model O Wireless's receiver
// (2022) and the Model O and Model D Wireless on their cables (2011, 2012), the Model O 2 PRO 4K/8K (201B cable, 2035
// receiver) and the Model D 2 PRO 4K/8K (201C, 2036). Each is read with the 0x83 feature exchange (MouseShapes.Request83)
// on its vendor collection with the unnumbered 64-byte feature report (FeatureLength 65 with the id byte): usage page
// 0xFFFF (usage 0 on the 4K/8K mice, 0 or 1 on the older ones) or 0xFFA0 usage 1 on the older receiver. The receiver needs
// about 100 ms between the request and the read (the query goes to the mouse over the air).
// From HaloBattery's glorious.py (MIT; measured on a 2022 receiver, HaloBattery #281, after GloriousBatteryMonitor-Go) and,
// for the product ids, the 4K/8K collection and the status codes, OpenMouse's glorious-classic and glorious-core2 modules
// (AGPL; facts only, from Glorious CORE's own device table and frame builders).
[BrandFamilies]
public static class GloriousMice
{
    public const int Vendor = 0x258A;
    public static readonly IReadOnlyDictionary<int, string> Products = new Dictionary<int, string>
    {
        [0x2022] = "Glorious Model O Wireless", [0x2011] = "Glorious Model O Wireless", [0x2012] = "Glorious Model D Wireless",
        [0x201B] = "Glorious Model O 2 PRO 4K/8K", [0x2035] = "Glorious Model O 2 PRO 4K/8K",
        [0x201C] = "Glorious Model D 2 PRO 4K/8K", [0x2036] = "Glorious Model D 2 PRO 4K/8K",
    };

    public static bool Takes(HidShape shape) => shape.Vendor == Vendor && Products.ContainsKey(shape.Product) && shape.FeatureLength == 65
        && (MouseShapes.Collection(shape, 0xFFFF, 0) || MouseShapes.Collection(shape, 0xFFFF, 1) || MouseShapes.Collection(shape, 0xFFA0, 1));

    public static IEnumerable<BrandFamily> Families =>
    [
        new("Glorious", Takes, _ => [new BrandWay("glorious.f83", WayTransport.Feature, MouseShapes.Request83(), MouseShapes.Battery83) { GapMs = 100, FeatureLength = 65 }])
        { Kind = _ => DeviceKind.Mouse, Prefix = "glorious", DisplayName = shape => Products.GetValueOrDefault(shape.Product) },
    ];
}
