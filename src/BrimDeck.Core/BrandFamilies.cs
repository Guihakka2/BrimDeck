using System.Reflection;

namespace BrimDeck.Core;

// What a USB HID collection says of itself, as the brand families match on it.
public readonly record struct HidShape(int Vendor, int Product, int UsagePage, int Usage, int Interface, int InputLength, int OutputLength,
    int FeatureLength, IReadOnlySet<byte> OutputIds, IReadOnlySet<byte> FeatureIds, string Name)
{
    public bool Declares(BrandWay way) => way.Transport switch
    {
        WayTransport.Output or WayTransport.OutputListen => OutputIds.Contains(way.Request.Length > 0 ? way.Request[0] : (byte)0),
        WayTransport.Feature or WayTransport.FeatureListen => FeatureIds.Contains(way.Request.Length > 0 ? way.Request[0] : (byte)0),
        WayTransport.Listen => InputLength > 0,
        _ => false
    };
}

// How a read goes over the collection:
// - Output: an output report (Request, report id first) is written; the answer comes as an input report (Parse).
// - Feature: a feature report is set (Request), then after GapMs got back (report id Request[0], FeatureLength bytes) (Parse).
// - FeatureListen: a feature report is set; the answer comes as an input report.
// - OutputListen: as Output, the request only waking the device into sending what it sends anyway.
// - Listen: nothing is sent; the device's own input reports are read for TimeoutMs (a battery it reports by itself).
public enum WayTransport { Output, Feature, FeatureListen, OutputListen, Listen }

// One of a brand's battery reads. Name: stable across versions (kept in device-reads.json), "brand.read". Before: frames sent
// first the same way (a session or mode a read needs), After: frames that undo them, sent whatever the answer. Attempts: how
// many times the request is sent before the read counts as unanswered. Parse: the reading in one report, or null for any
// other report (another command's answer, the request echoed, a notice). StripZero: input reports handed over without a
// leading zero report id.
public sealed record BrandWay(string Name, WayTransport Transport, byte[] Request, Func<byte[], BatteryRead?> Parse)
{
    public int TimeoutMs { get; init; } = 600;
    public int Attempts { get; init; } = 2;
    public int GapMs { get; init; } = 30;
    public int FeatureLength { get; init; }
    public IReadOnlyList<byte[]> Before { get; init; } = [];
    public IReadOnlyList<byte[]> After { get; init; } = [];
    public bool StripZero { get; init; }
}

// A brand's devices and their reads. Takes: whether a collection is the brand's to read (its vendor, and for a vendor id
// other brands share, its products), from its shape alone, never by sending anything. Ways: the reads that collection takes,
// best first; a way is only sent to a collection that declares its report id (HidShape.Declares). Kind: what the device is
// when its name does not say. Prefix: the device key's prefix (stable: user choices are kept under the key). DisplayName: a
// better name than the product string, if any.
public sealed record BrandFamily(string Brand, Func<HidShape, bool> Takes, Func<HidShape, IReadOnlyList<BrandWay>> Ways)
{
    public Func<HidShape, DeviceKind?> Kind { get; init; } = _ => null;
    public string Prefix { get; init; } = "brand";
    public Func<HidShape, string?> DisplayName { get; init; } = _ => null;
    // Read only while none of these processes runs (the brand's own software would lose its answers to us).
    public IReadOnlyList<string> StandAsideFor { get; init; } = [];
}

// Marks a class whose static Families (IEnumerable<BrandFamily>) are brand families: each brand lives in its own file, so
// adding one touches no other.
[AttributeUsage(AttributeTargets.Class)]
public sealed class BrandFamiliesAttribute : Attribute;

// Every brand family, in the order of their brands' names.
public static class BrandFamilies
{
    private static readonly Lazy<IReadOnlyList<BrandFamily>> Loaded = new(() => [.. typeof(BrandFamilies).Assembly.GetTypes()
        .Where(type => type.GetCustomAttribute<BrandFamiliesAttribute>() is not null)
        .OrderBy(type => type.Name, StringComparer.Ordinal)
        .SelectMany(type => type.GetProperty("Families", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as IEnumerable<BrandFamily> ?? [])]);

    public static IReadOnlyList<BrandFamily> All => Loaded.Value;

    // The family a collection belongs to, and the reads it takes there that it declares; null when no family takes it.
    public static (BrandFamily Family, IReadOnlyList<BrandWay> Ways)? Find(HidShape shape)
    {
        foreach (var family in All)
        {
            if (!family.Takes(shape)) continue;
            var ways = family.Ways(shape).Where(shape.Declares).ToList();
            return (family, ways);
        }
        return null;
    }

    // A frame of the given length, starting with the given bytes.
    public static byte[] Frame(int length, params byte[] head) { var frame = new byte[Math.Max(length, head.Length)]; head.CopyTo(frame, 0); return frame; }
}
