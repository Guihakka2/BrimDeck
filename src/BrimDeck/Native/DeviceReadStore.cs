using System.IO;
using System.Text.Json;
using BrimDeck.Core;

namespace BrimDeck.Native;

// The kept reads (DeviceReadBook) on disk, in the app's data folder (device-reads.json), and the guard around trying a read
// a device has not answered before: a mark (device-probe.json) is written through to the disk before the read is sent and
// removed when it returns. A mark left at the next start means the app ended while the device was being asked (the read hung
// it, or the machine froze): that read is never sent to that device again (DeviceReadBook.Harmed). Reads already kept are
// not marked, so a device in use costs no disk write per reading.
internal static class DeviceReadStore
{
    private const string BookFile = "device-reads.json", ProbeFile = "device-probe.json";
    private static readonly object Gate = new();
    private static string? _folder;
    private static DeviceReadBook _book = new();

    public static DeviceReadBook Book { get { lock (Gate) return _book; } }

    // Once, before the devices are read.
    public static void Start(string folder)
    {
        lock (Gate)
        {
            _folder = folder;
            _book = DeviceReadBook.Parse(Read(BookFile));
            if (Read(ProbeFile) is { } left)
            {
                try
                {
                    var mark = JsonSerializer.Deserialize<Mark>(left);
                    if (mark is { Device.Length: > 0, Way.Length: > 0 }) _book.Harmed(mark.Device, mark.Way);
                }
                catch (JsonException) { }
                Delete(ProbeFile);
            }
            SaveLocked();
        }
    }

    // Writes the book when it changed. Called after a pass, on the worker.
    public static void Save() { lock (Gate) if (_book.Dirty) SaveLocked(); }

    private sealed record Mark(string Device, string Way);

    // Runs a first try of a read on a device with the mark on disk around it.
    public static T Probe<T>(string device, string way, Func<T> ask)
    {
        lock (Gate) Write(ProbeFile, JsonSerializer.Serialize(new Mark(device, way)), through: true);
        try { return ask(); }
        finally { lock (Gate) Delete(ProbeFile); }
    }

    private static void SaveLocked() { if (_book.Dirty || _folder is not null && !File.Exists(Path.Combine(_folder, BookFile))) Write(BookFile, _book.Serialize(), through: false); }

    private static string? Read(string name)
    {
        if (_folder is null) return null;
        try { var path = Path.Combine(_folder, name); return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
    private static void Write(string name, string text, bool through)
    {
        if (_folder is null) return;
        try
        {
            Directory.CreateDirectory(_folder);
            var path = Path.Combine(_folder, name);
            var temporary = path + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, through ? FileOptions.WriteThrough : FileOptions.None))
            using (var writer = new StreamWriter(stream)) writer.Write(text);
            File.Move(temporary, path, overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    private static void Delete(string name)
    {
        if (_folder is null) return;
        try { File.Delete(Path.Combine(_folder, name)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
