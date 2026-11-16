using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using BrimDeck.Core;
using BrimDeck.Core.ClipboardSync;
using BrimDeck.Native;

namespace BrimDeck;

internal sealed class ClipboardSyncAccounts
{
    public string DeviceName { get; set; } = "BrimDeck " + Environment.MachineName;
    public WeTypeIdentity WeType { get; set; } = new();
    public DoubaoIdentity? Doubao { get; set; }
    public LanClipboardPeer? Lan { get; set; }
    public string DoubaoDeviceId { get; set; } = (1_000_000_000_000_000L + RandomNumberGenerator.GetInt32(1_000_000_000) * 1_000_000L + RandomNumberGenerator.GetInt32(1_000_000)).ToString();
}

internal sealed partial class ClipboardSyncService : IDisposable
{
    private readonly App _app;
    private readonly string _file;
    private ClipboardWatch? _watch;
    private readonly Dictionary<bool, CancellationTokenSource> _workers = [];
    private readonly Dictionary<bool, string> _pending = [];
    private readonly Queue<(string Hash, DateTime Time)> _received = [];
    private bool _disposed;
    public bool TestMode { get; } = Environment.GetCommandLineArgs().Contains("--clipboard-sync-test", StringComparer.Ordinal);
    public string Folder { get; }
    public ClipboardSyncAccounts Accounts { get; private set; } = new();
    public string WeTypeStatus { get; private set; } = "";
    public string DoubaoStatus { get; private set; } = "";
    public DoubaoDeviceState? DoubaoCapability { get; private set; }
    public static string DoubaoUnavailableMessage => Loc.T("账号已登录，但服务端尚未允许本机同步。", "Signed in, but the server has not enabled clipboard sync for this device.");
    public event Action? Changed;

    public ClipboardSyncService(App app, string folder)
    {
        _app = app; Folder = Path.Combine(folder, "clipboard-sync"); _file = Path.Combine(Folder, "accounts.dat");
        try
        {
            if (File.Exists(_file))
            {
                var plain = ProtectedData.Unprotect(File.ReadAllBytes(_file), null, DataProtectionScope.CurrentUser);
                try { Accounts = JsonSerializer.Deserialize<ClipboardSyncAccounts>(plain) ?? new(); }
                finally { CryptographicOperations.ZeroMemory(plain); }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        { WeTypeStatus = DoubaoStatus = Loc.T("连接信息无法读取，请重新连接。", "Connection details could not be read. Connect again."); }
    }
    public void Save()
    {
        Directory.CreateDirectory(Folder);
        var plain = JsonSerializer.SerializeToUtf8Bytes(Accounts);
        try
        {
            File.WriteAllBytes(_file + ".tmp", ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser));
            File.Move(_file + ".tmp", _file, true);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public async Task RefreshWeTypeNameAsync()
    {
        if (!Connected(false) || _workers.ContainsKey(false)) return;
        using var client = new WeTypeSyncClient(Accounts.WeType, Save);
        var names = await client.ReadOwnNamesAsync(_app.Lifetime.Token);
        if (Accounts.WeType.DisplayName != names.Name) { Accounts.WeType.DisplayName = names.Name; Save(); Changed?.Invoke(); }
    }
    public void RenameDevice(string name)
    {
        name = name.Trim();
        if (name.Length is 0 or > 48 || name.Any(char.IsControl)) throw new ArgumentException("Invalid device name.");
        Stop(false); Stop(true);
        Accounts.DeviceName = name; Accounts.WeType.DeviceName = name;
        if (Accounts.Doubao is not null) Accounts.Doubao.DeviceName = name;
        Save(); Apply(); Changed?.Invoke();
    }
    public bool Connected(bool doubao) => doubao ? Accounts.Doubao is not null : Accounts.WeType.Paired;
    public string Status(bool doubao) => doubao ? DoubaoStatus : WeTypeStatus;
    private void Report(bool doubao, string message)
    {
        if (_disposed) return;
        if (doubao) DoubaoStatus = message; else WeTypeStatus = message;
        Changed?.Invoke();
    }
    public async Task PairWeTypeAsync(string code, CancellationToken ct)
    {
        if (_workers.ContainsKey(false)) throw new InvalidOperationException("Disconnect before pairing again.");
        Accounts.WeType.DeviceName = Accounts.DeviceName;
        using var client = new WeTypeSyncClient(Accounts.WeType, Save);
        Report(false, Loc.T("请在微信输入法中确认配对…", "Confirm the pairing in WeType…"));
        await client.PairAsync(code, ct);
        Report(false, Loc.T("已配对", "Paired"));
    }
    public async Task ConnectDoubaoAsync(DoubaoIdentity identity, CancellationToken ct)
    {
        using var client = new DoubaoSyncClient(identity);
        // Authentication and the provider's clipboard capability are separate states.
        var capability = await client.ReadCurrentDeviceAsync(ct);
        ct.ThrowIfCancellationRequested();
        Accounts.Doubao = identity; Save();
        DoubaoCapability = capability;
        Report(true, capability.CanSync ? Loc.T("已登录", "Signed in") : DoubaoUnavailableMessage);
    }
    public async Task<DoubaoDeviceState> CheckDoubaoCapabilityAsync(CancellationToken ct)
    {
        var identity = Accounts.Doubao ?? throw new InvalidOperationException();
        using var client = new DoubaoSyncClient(identity);
        var capability = await client.ReadCurrentDeviceAsync(ct);
        ct.ThrowIfCancellationRequested();
        if (!ReferenceEquals(Accounts.Doubao, identity)) throw new OperationCanceledException();
        DoubaoCapability = capability;
        Changed?.Invoke();
        return capability;
    }
    public void Disconnect(bool doubao)
    {
        Stop(doubao);
        if (doubao)
        {
            Accounts.Doubao = null;
            DoubaoCapability = null;
        }
        else Accounts.WeType = new() { DeviceName = Accounts.DeviceName };
        Save(); Report(doubao, Loc.T("已断开", "Disconnected"));
    }
    public void Apply()
    {
        if (_disposed || TestMode) return;
        ApplyLan();
        foreach (bool doubao in new[] { false, true })
        {
            bool enabled = doubao ? _app.Settings.DoubaoClipboardSync : _app.Settings.WeTypeClipboardSync;
            if (enabled && Connected(doubao) && !_workers.ContainsKey(doubao))
            {
                var stop = CancellationTokenSource.CreateLinkedTokenSource(_app.Lifetime.Token);
                _workers[doubao] = stop; _ = RunAsync(doubao, stop);
            }
            else if ((!enabled || !Connected(doubao)) && _workers.ContainsKey(doubao)) Stop(doubao);
        }
        if ((_workers.Count > 0 || _lanStop is not null) && _watch is null)
        {
            try { _watch = new ClipboardWatch(Copied, name => ClipboardExclusions.Excludes(_app.Settings.ClipboardExcluded, name)); }
            catch (System.ComponentModel.Win32Exception) { Report(false, Loc.T("无法监听剪贴板", "Cannot watch the clipboard")); Report(true, WeTypeStatus); }
        }
        else ReleaseIdleWatcher();
    }
    private void ReleaseIdleWatcher()
    {
        if (_workers.Count != 0 || _lanStop is not null) return;
        _watch?.Dispose();
        _watch = null;
    }
    public async Task<bool> TestMarkerAsync(bool doubao, string marker, bool send, CancellationToken cancellation)
    {
        const string prefix = "BrimDeck-Sync-Test-";
        if (!TestMode || !Connected(doubao)) throw new InvalidOperationException();
        if (!marker.StartsWith(prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(marker.AsSpan(prefix.Length), "N", out _))
            throw new ArgumentException(nameof(marker));

        using IClipboardSyncClient client = doubao
            ? new DoubaoSyncClient(Accounts.Doubao!)
            : new WeTypeSyncClient(Accounts.WeType, Save);
        await client.ConnectAsync(cancellation);
        if (!send)
            return string.Equals((await client.ReceiveAsync(cancellation))?.Text, marker, StringComparison.Ordinal);

        await client.SendAsync(marker, cancellation);
        return true;
    }
    private void Stop(bool doubao)
    {
        if (_workers.Remove(doubao, out var source)) source.Cancel();
        _pending.Remove(doubao);
    }
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private void Copied(ClipCopy copy)
    {
        while (_received.TryPeek(out var item) && item.Time < DateTime.UtcNow.AddMinutes(-2)) _received.Dequeue();
        if (copy.Png is { } png)
        {
            if (_lanImages && _lanOutgoing is { } images && LanPng.Fits(copy.PixelWidth, copy.PixelHeight, png.Length))
            {
                string hash = HashImage(png);
                if (!_received.Any(item => item.Hash == hash)) images.Writer.TryWrite(copy);
            }
            return;
        }
        if (copy.Text.Length == 0 || LanClipboardLink.ContainsInvitation(copy.Text)) return;
        if (_received.Count > 0)
        {
            string hash = Hash(copy.Text);
            if (_received.Any(item => item.Hash == hash)) return;
        }
        foreach (var provider in _workers.Keys) _pending[provider] = copy.Text;
        if (_lanOutgoing is { } outgoing && Encoding.UTF8.GetByteCount(copy.Text) <= SyncTransport.MaxTextBytes)
            outgoing.Writer.TryWrite(copy);
    }
    private void AcceptText(string text, string source)
    {
        _app.Shelf.AcceptRemoteText(text, source);
        _watch?.IgnoreCurrent();
        RememberReceived(Hash(text));
    }
    private void RememberReceived(string hash)
    {
        _received.Enqueue((hash, DateTime.UtcNow));
        while (_received.Count > 64) _received.Dequeue();
    }
    private static string HashImage(byte[] png) => "PNG:" + Convert.ToHexString(SHA256.HashData(png));
    private async Task RunAsync(bool doubao, CancellationTokenSource stop)
    {
        using IClipboardSyncClient client = doubao ? new DoubaoSyncClient(Accounts.Doubao!) : new WeTypeSyncClient(Accounts.WeType, Save);
        bool initialized = false; string? lastId = null; int failures = 0;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    if (!initialized)
                    {
                        Report(doubao, Loc.T("正在连接…", "Connecting…"));
                        DoubaoDeviceState? capability = null;
                        if (client is DoubaoSyncClient doubaoClient)
                        {
                            capability = await doubaoClient.ReadCurrentDeviceAsync(stop.Token);
                            if (!capability.CanSync)
                                throw new DoubaoDeviceUnavailableException(capability.SyncState, capability.SupportsCopy);
                        }
                        else
                            await client.ConnectAsync(stop.Token);
                        stop.Token.ThrowIfCancellationRequested();
                        // Establish a baseline: enabling sync never pastes content left on the server before connection.
                        lastId = (await client.ReceiveAsync(stop.Token))?.Id;
                        stop.Token.ThrowIfCancellationRequested();
                        if (capability is not null) DoubaoCapability = capability;
                        initialized = true;
                    }
                    if (_pending.Remove(doubao, out var text))
                    {
                        try { await client.SendAsync(text, stop.Token); }
                        catch { Report(doubao, Loc.T("发送未确认；不会自动重发，请重新复制。", "Send was not confirmed. Copy again to retry.")); throw; }
                        Report(doubao, Loc.T("已发送文字 · ", "Text sent · ") + DateTime.Now.ToString("HH:mm:ss"));
                    }
                    uint sequence = ClipboardWatch.GetClipboardSequenceNumber();
                    var remote = await client.ReceiveAsync(stop.Token);
                    stop.Token.ThrowIfCancellationRequested();
                    if (remote is not null && remote.Id != lastId)
                    {
                        if (sequence == ClipboardWatch.GetClipboardSequenceNumber() && !_pending.ContainsKey(doubao))
                        {
                            // Prevent both providers and the history watcher from sending a received copy back.
                            AcceptText(remote.Text, doubao ? "豆包输入法" : "微信输入法");
                            Report(doubao, Loc.T("已接收文字 · ", "Text received · ") + DateTime.Now.ToString("HH:mm:ss"));
                        }
                        lastId = remote.Id;
                    }
                    else if (failures > 0 || Status(doubao) == Loc.T("正在连接…", "Connecting…")) Report(doubao, Loc.T("正在同步文字", "Syncing text"));
                    failures = 0;
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch (DoubaoDeviceUnavailableException ex)
                {
                    if (stop.IsCancellationRequested) break;
                    DoubaoCapability = new(ex.SyncState, ex.SupportsCopy);
                    Report(doubao, DoubaoUnavailableMessage);
                    break;
                }
                catch (DoubaoRequestException ex)
                {
                    Report(doubao, Loc.T("豆包同步请求被拒绝，错误码：", "Doubao rejected the sync request. Error code: ") + ex.Code);
                    break;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    failures = Math.Min(failures + 1, 5); initialized = false;
                    Report(doubao, Loc.T("同步暂时失败，请检查网络或重新连接。", "Sync failed. Check your connection or reconnect."));
                }
                await Task.Delay(TimeSpan.FromSeconds(failures == 0 ? 3 : Math.Min(60, 3 * (1 << failures))), stop.Token);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (_workers.TryGetValue(doubao, out var current) && ReferenceEquals(current, stop))
            {
                _workers.Remove(doubao);
                _pending.Remove(doubao);
                ReleaseIdleWatcher();
            }
            stop.Dispose();
        }
    }
    public void Dispose()
    {
        _disposed = true;
        StopLan();
        foreach (var source in _workers.Values) source.Cancel();
        _workers.Clear(); _pending.Clear(); _watch?.Dispose(); _watch = null;
    }
}
