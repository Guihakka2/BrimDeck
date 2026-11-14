using BrimDeck.Core;

// Which read a device is asked (DeviceReadBook): the kept read alone, a limited search otherwise, a check after many misses,
// reads that went wrong never again, and all of it kept across runs and versions.
static class DeviceReadTests
{
    public static void Run(Action<string, bool> check)
    {
        const string device = "3837:1014", mouse = "FF01:0001:2", other = "FF0B:0104:2";
        string[] ways = ["mchose.realtek", "mchose.frame4d", "mchose.status"];
        var at = DateTimeOffset.Parse("2026-11-14T12:00:00+08:00");

        var book = new DeviceReadBook();
        var first = book.Plan(device, mouse, ways, 0, keptPresent: false);
        check("Device reads: an unknown device is tried with the reads its collection takes, in order",
            first.Kind == DeviceReadBook.Kind.Probe && first.Ways.SequenceEqual(ways));
        book.Silent(device, mouse, first.Kind, 0);
        check("Device reads: an unanswered search waits 5 minutes, then 30, then stops until the device comes again",
            book.Plan(device, mouse, ways, 60_000, false).Kind == DeviceReadBook.Kind.None
            && book.Plan(device, mouse, ways, 5 * 60_000, false).Kind == DeviceReadBook.Kind.Probe);
        book.Silent(device, mouse, DeviceReadBook.Kind.Probe, 5 * 60_000);
        bool thirtyLater = book.Plan(device, mouse, ways, 35 * 60_000, false).Kind == DeviceReadBook.Kind.Probe;
        book.Silent(device, mouse, DeviceReadBook.Kind.Probe, 35 * 60_000);
        bool stopped = book.Plan(device, mouse, ways, 10L * 24 * 3600_000, false).Kind == DeviceReadBook.Kind.None;
        book.Arrived(device);
        check("Device reads: after three searches nothing more is sent, and a device plugged in again is searched again",
            thirtyLater && stopped && book.Plan(device, mouse, ways, 10L * 24 * 3600_000, false).Kind == DeviceReadBook.Kind.Probe);

        book.Answered(device, mouse, "mchose.frame4d", at);
        var kept = book.Plan(device, mouse, ways, 0, keptPresent: true);
        check("Device reads: the read that answered is the only one sent, and only to its collection",
            kept.Kind == DeviceReadBook.Kind.Kept && kept.Ways.SequenceEqual(["mchose.frame4d"])
            && book.Plan(device, other, ways, 0, keptPresent: true).Kind == DeviceReadBook.Kind.None && book.Dirty);

        var saved = DeviceReadBook.Parse(book.Serialize());
        check("Device reads: what was found is kept across runs (and a new version), and the book is clean once written",
            !book.Dirty && saved.Entry(device)?.Way == "mchose.frame4d" && saved.Entry(device)?.Collection == mouse
            && saved.Plan(device, mouse, ways, 0, true).Ways.SequenceEqual(["mchose.frame4d"]));
        check("Device reads: a kept read this version no longer has is searched for afresh",
            saved.Plan(device, mouse, ["mchose.realtek", "mchose.status"], 0, true).Kind == DeviceReadBook.Kind.Probe);
        check("Device reads: with the kept collection gone (another receiver of the same id), the device is searched where it is",
            saved.Plan(device, other, ways, 0, keptPresent: false).Kind == DeviceReadBook.Kind.Probe);

        for (int i = 0; i < DeviceReadBook.MissesBeforeRecheck; i++) saved.Silent(device, mouse, DeviceReadBook.Kind.Kept, 0);
        var recheck = saved.Plan(device, mouse, ways, 0, true);
        saved.Silent(device, mouse, recheck.Kind, 0);
        check("Device reads: after 20 misses in a row the kept read is checked once against the others, then not for 30 minutes",
            recheck.Kind == DeviceReadBook.Kind.Recheck && recheck.Ways[0] == "mchose.frame4d" && recheck.Ways.Count == 3
            && saved.Plan(device, mouse, ways, 60_000, true).Kind == DeviceReadBook.Kind.Kept);
        saved.Answered(device, mouse, "mchose.status", at.AddDays(1));
        check("Device reads: another read that answers replaces the kept one (the old one stops being sent)",
            saved.Plan(device, mouse, ways, 0, true).Ways.SequenceEqual(["mchose.status"]));

        bool stops = saved.Harmed(device, "mchose.status");
        var after = saved.Plan(device, mouse, ways, 0, true);
        check("Device reads: a read that went wrong is never sent again, and the kept one it was is forgotten",
            !stops && after.Kind == DeviceReadBook.Kind.Probe && !after.Ways.Contains("mchose.status") && saved.Entry(device)?.Way is null);
        check("Device reads: a second read going wrong stops the device altogether, kept across runs",
            saved.Harmed(device, "mchose.realtek") && saved.Plan(device, mouse, ways, 0, true).Kind == DeviceReadBook.Kind.None
            && DeviceReadBook.Parse(saved.Serialize()).Stopped(device));
        saved.Resume(device);
        check("Device reads: the user turning the reading on again forgets what went wrong",
            !saved.Stopped(device) && saved.Plan(device, mouse, ways, 0, false).Ways.SequenceEqual(ways));

        check("Device reads: a damaged or empty file is an empty book, never an error",
            DeviceReadBook.Parse("{not json").Entry(device) is null && DeviceReadBook.Parse("").Entry(device) is null
            && DeviceReadBook.Parse("[{\"Device\":\"\"},null]").Entry("") is null);

        DeviceReadSwitches.Use(["3837:1014", ""]);
        bool off = DeviceReadSwitches.IsOff("3837:1014") && !DeviceReadSwitches.IsOff("3837:100B");
        DeviceReadSwitches.Use([]);
        var choices = new List<DeviceChoice> { new() { Key = "a", Hardware = "3837:1014", Read = false }, new() { Key = "b", Hardware = "046D:C548" }, new() { Key = "c", Read = false } };
        check("Device reads: the devices the user turned off, by their hardware",
            off && DeviceLists.ReadOff(choices).SequenceEqual(["3837:1014"]) && !DeviceReadSwitches.IsOff("3837:1014"));
    }
}
