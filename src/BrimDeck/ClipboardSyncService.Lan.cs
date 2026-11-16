using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using System.Windows.Media.Imaging;
using BrimDeck.Core;
using BrimDeck.Core.ClipboardSync;
using BrimDeck.Native;

namespace BrimDeck;

internal sealed partial class ClipboardSyncService
{
    private CancellationTokenSource? _lanStop;
    private Channel<ClipCopy>? _lanOutgoing;
    private bool _lanImages;
    public string LanStatus { get; private set; } = "";

    public void SetLanPeer(LanClipboardPeer? peer)
    {
        StopLan();
        var previous = Accounts.Lan;
        Accounts.Lan = peer;
        try { Save(); }
        catch
        {
            Accounts.Lan = previous;
            Apply();
            throw;
        }
        Apply();
        Changed?.Invoke();
    }

    private void ApplyLan()
    {
        if (!_app.Settings.LanClipboardSync || Accounts.Lan is not { } peer)
        {
            StopLan();
            return;
        }
        if (_lanStop is not null) return;
        var stop = CancellationTokenSource.CreateLinkedTokenSource(_app.Lifetime.Token);
        _lanStop = stop;
        _ = RunLanAsync(peer, stop);
    }

    private void StopLan()
    {
        var stop = _lanStop;
        _lanStop = null;
        _lanOutgoing = null;
        _lanImages = false;
        stop?.Cancel();
    }

    private async Task RunLanAsync(LanClipboardPeer peer, CancellationTokenSource stop)
    {
        void Status(string message)
        {
            if (_disposed || !ReferenceEquals(_lanStop, stop)) return;
            LanStatus = message;
            Changed?.Invoke();
        }

        try
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    Status(Loc.T("等待局域网设备连接…", "Waiting for the LAN device…"));
                    await LanClipboardLink.RunWithImagesAsync(peer, async (stream, images, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        Status(images
                            ? Loc.T("已连接 · 正在同步文字和图片", "Connected · Syncing text and images")
                            : Loc.T("已连接 · 对端仅支持文字同步", "Connected · The other PC supports text only"));
                        try { await ExchangeLanAsync(stream, images, token); }
                        finally { Status(Loc.T("等待局域网设备重新连接…", "Waiting for the LAN device to reconnect…")); }
                    }, stop.Token);
                }
                catch (LanHostAddressUnavailableException)
                {
                    Status(Loc.T("配对时的本机地址暂不可用。若地址已变更，请在两端重新配对。", "The local address used for pairing is unavailable. If it changed, pair again on both PCs."));
                }
                catch (LanPeerOffSubnetException)
                {
                    Status(Loc.T("当前没有与对端同子网的连接。若邀请方地址已变更，请重新配对。", "No local connection shares the other PC's subnet. If the inviting PC's address changed, pair again."));
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or SocketException or AuthenticationException
                    or OperationCanceledException or CryptographicException or FormatException
                    or System.Net.NetworkInformation.NetworkInformationException)
                {
                    if (stop.IsCancellationRequested) break;
                    Status(Loc.T("暂时无法连接 · 正在重试", "Cannot connect yet · Retrying"));
                }
                await Task.Delay(TimeSpan.FromSeconds(3), stop.Token);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_lanStop, stop))
            {
                _lanStop = null;
                ReleaseIdleWatcher();
            }
            stop.Dispose();
        }
    }

    private async Task ExchangeLanAsync(SslStream stream, bool images, CancellationToken cancellation)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var outgoing = Channel.CreateBounded<ClipCopy>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest
        });
        _lanOutgoing = outgoing;
        _lanImages = images;
        Task read = ReadAsync();
        Task write = WriteAsync();
        await Task.WhenAny(read, write);
        session.Cancel();
        if (ReferenceEquals(_lanOutgoing, outgoing))
        {
            _lanOutgoing = null;
            _lanImages = false;
        }
        await Task.WhenAll(read, write);

        async Task ReadAsync()
        {
            string? last = null;
            while (true)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                var content = await LanClipboardLink.ReadContentAsync(stream, images, timeout.Token);
                session.Token.ThrowIfCancellationRequested();
                if (content is null) continue;
                string source = Loc.T("局域网设备", "LAN device");
                if (content is LanClipboardContent.Text(var text))
                {
                    string hash = Hash(text);
                    if (LanClipboardLink.ContainsInvitation(text) || hash == last) continue;
                    if (!outgoing.Reader.TryPeek(out _))
                    {
                        try { AcceptText(text, source); }
                        catch (ExternalException) { /* The clipboard is currently locked by another application. */ }
                    }
                    last = hash;
                }
                else if (content is LanClipboardContent.Image(var png))
                {
                    string hash = HashImage(png.Bytes);
                    if (hash == last || outgoing.Reader.TryPeek(out _)) continue;
                    uint sequence = ClipboardWatch.GetClipboardSequenceNumber();
                    BitmapSource bitmap;
                    try { bitmap = await Task.Run(() => ShelfService.DecodeRemoteImage(png), timeout.Token); }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or FileFormatException or ArgumentException or NotSupportedException or InvalidOperationException) { continue; }
                    timeout.Token.ThrowIfCancellationRequested();
                    if (sequence == ClipboardWatch.GetClipboardSequenceNumber() && !outgoing.Reader.TryPeek(out _) &&
                        _app.Shelf.AcceptRemoteImage(png, bitmap, source))
                    {
                        _watch?.IgnoreCurrent();
                        RememberReceived(hash);
                    }
                    last = hash;
                }
            }
        }

        async Task WriteAsync()
        {
            string? last = null;
            while (true)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
                idle.CancelAfter(TimeSpan.FromSeconds(5));
                ClipCopy? copy = null;
                try { copy = await outgoing.Reader.ReadAsync(idle.Token); }
                catch (OperationCanceledException) when (!session.IsCancellationRequested) { }
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                if (copy is null)
                {
                    await LanClipboardLink.KeepAliveAsync(stream, timeout.Token);
                    continue;
                }
                if (copy.Png is { } bytes)
                {
                    if (!images) continue;
                    string hash = HashImage(bytes);
                    if (hash == last) continue;
                    LanPng png;
                    try { png = await Task.Run(() => LanPng.Validate(bytes, timeout.Token), timeout.Token); }
                    catch (Exception ex) when (ex is IOException or InvalidDataException) { continue; }
                    await LanClipboardLink.WriteImageAsync(stream, png, timeout.Token);
                    last = hash;
                    continue;
                }
                string textHash = Hash(copy.Text);
                if (textHash == last) continue;
                try { await LanClipboardLink.WriteTextAsync(stream, copy.Text, timeout.Token); }
                catch (EncoderFallbackException) { continue; }
                last = textHash;
            }
        }
    }
}
