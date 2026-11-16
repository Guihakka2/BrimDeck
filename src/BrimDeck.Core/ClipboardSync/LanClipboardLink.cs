using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace BrimDeck.Core.ClipboardSync;

public sealed record LanClipboardPeer(string Address, int Port, byte[] Fingerprint, byte[] Key, byte[]? HostCertificate)
{
    public bool IsHost => HostCertificate is not null;
}

public abstract record LanClipboardContent
{
    public sealed record Text(string Value) : LanClipboardContent;
    public sealed record Image(LanPng Png) : LanClipboardContent;
}

public static class LanClipboardLink
{
    private const string InvitePrefix = "BD-LAN-1.";
    private const int KeySize = 32;
    private const byte Pair = 1, Sync = 2, SyncWithImages = 3;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(8);

    public static IReadOnlyList<IPAddress> LocalAddresses() => LanNetwork.Interfaces().Select(n => n.Address).Distinct().ToArray();
    public static bool ContainsInvitation(string text) => text.Contains(InvitePrefix, StringComparison.Ordinal);

    private static IPAddress RequireHostAddress(IPAddress address) =>
        LanNetwork.Interfaces().Any(adapter => adapter.Address.Equals(address))
            ? address : throw new LanHostAddressUnavailableException();

    public sealed class Invitation : IDisposable
    {
        private readonly IPAddress _address;
        private readonly TcpListener _listener;
        private readonly int _port;
        private readonly X509Certificate2 _certificate;
        private readonly byte[] _secret = RandomNumberGenerator.GetBytes(KeySize);
        private readonly byte[] _pin;

        public Invitation(IPAddress address)
        {
            _address = RequireHostAddress(address);
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=BrimDeck LAN", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
            _certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
            _pin = _certificate.GetCertHash(HashAlgorithmName.SHA256);
            try
            {
                _listener = Listen(_address, 0);
                _port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            }
            catch { _certificate.Dispose(); throw; }
        }

        public int Port => _port;

        public string Code
        {
            get
            {
                byte[] payload = new byte[6 + 2 * KeySize];
                _address.GetAddressBytes().CopyTo(payload, 0);
                BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(4), (ushort)_port);
                _pin.CopyTo(payload, 6);
                _secret.CopyTo(payload, 6 + KeySize);
                return InvitePrefix + Convert.ToBase64String(payload);
            }
        }

        public async Task<LanClipboardPeer> AcceptAsync(CancellationToken cancellation)
        {
            using var network = new NetworkLifetime(cancellation);
            using var expiry = CancellationTokenSource.CreateLinkedTokenSource(network.Token);
            expiry.CancelAfter(TimeSpan.FromMinutes(2));
            try
            {
                while (true)
                {
                    using var peer = await _listener.AcceptTcpClientAsync(expiry.Token);
                    using var timeout = Timed(expiry.Token);
                    try
                    {
                        ValidateRemote(peer, _address);
                        using var stream = await SecureServerAsync(peer, _certificate, timeout.Token);
                        await ReadProofAsync(stream, Pair, _secret, timeout.Token);
                        var key = RandomNumberGenerator.GetBytes(KeySize);
                        var config = new LanClipboardPeer(_address.ToString(), _port, _pin, key,
                            _certificate.Export(X509ContentType.Pkcs12));
                        await stream.WriteAsync(key, timeout.Token);
                        return config;
                    }
                    catch (Exception ex) when (RejectedConnection(ex) && !expiry.IsCancellationRequested) { }
                }
            }
            catch (OperationCanceledException) when (network.Token.IsCancellationRequested && !cancellation.IsCancellationRequested)
            { throw new LanNetworkChangedException(); }
            finally { _listener.Stop(); }
        }

        public void Dispose()
        {
            _listener.Stop();
            _certificate.Dispose();
            CryptographicOperations.ZeroMemory(_secret);
        }
    }

    public static async Task<LanClipboardPeer> JoinAsync(string code, CancellationToken cancellation)
    {
        code = code.Trim();
        if (!code.StartsWith(InvitePrefix, StringComparison.Ordinal) || code.Length != InvitePrefix.Length + 96)
            throw new FormatException("Invalid BrimDeck invitation.");
        byte[] payload = Convert.FromBase64String(code[InvitePrefix.Length..]);
        if (payload.Length != 6 + 2 * KeySize) throw new FormatException("Invalid BrimDeck invitation.");
        var address = new IPAddress(payload.AsSpan(0, 4));
        int port = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(4, 2));
        if (port == 0) throw new FormatException("Invalid BrimDeck port.");
        var pin = payload.AsSpan(6, KeySize).ToArray();
        byte[] key = new byte[KeySize];
        try
        {
            using var peer = await ConnectAsync(address, port, pin, async (stream, token) =>
            {
                await WriteProofAsync(stream, Pair, payload.AsMemory(6 + KeySize), token);
                await stream.ReadExactlyAsync(key, token);
            }, cancellation);
            return new(address.ToString(), port, pin, key, null);
        }
        finally { CryptographicOperations.ZeroMemory(payload); }
    }

    public static async Task RunAsync(LanClipboardPeer config, Func<SslStream, CancellationToken, Task> session,
        CancellationToken cancellation)
    {
        using var network = new NetworkLifetime(cancellation);
        await RunOnCurrentNetworkAsync(config, false, (stream, _, token) => session(stream, token), network.Token);
    }

    public static async Task RunWithImagesAsync(LanClipboardPeer config,
        Func<SslStream, bool, CancellationToken, Task> session, CancellationToken cancellation)
    {
        using var network = new NetworkLifetime(cancellation);
        await RunOnCurrentNetworkAsync(config, true, session, network.Token);
    }

    private static async Task RunOnCurrentNetworkAsync(LanClipboardPeer config, bool allowImages,
        Func<SslStream, bool, CancellationToken, Task> session,
        CancellationToken cancellation)
    {
        var address = IPAddress.Parse(config.Address);
        if (config.IsHost)
        {
            using var cert = X509CertificateLoader.LoadPkcs12(config.HostCertificate!, null);
            var listener = Listen(RequireHostAddress(address), config.Port);
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    using var peer = await listener.AcceptTcpClientAsync(cancellation);
                    using var timeout = Timed(cancellation);
                    try
                    {
                        ValidateRemote(peer, address);
                        using var stream = await SecureServerAsync(peer, cert, timeout.Token);
                        byte mode = await ReadProofAsync(stream, Sync, config.Key, timeout.Token, allowImages);
                        await stream.WriteAsync(new[] { mode }, timeout.Token);
                        await session(stream, mode == SyncWithImages, cancellation);
                    }
                    catch (Exception ex) when (RejectedConnection(ex) && !cancellation.IsCancellationRequested) { }
                }
            }
            finally { listener.Stop(); }
        }
        else
        {
            byte mode = allowImages ? SyncWithImages : Sync;
            ConnectedPeer peer;
            try { peer = await ConnectSyncAsync(mode); }
            catch (IOException ex) when (allowImages && ex is not LanPeerOffSubnetException && !cancellation.IsCancellationRequested)
            {
                mode = Sync;
                peer = await ConnectSyncAsync(mode);
            }
            using (peer) await session(peer.Stream, mode == SyncWithImages, cancellation);

            Task<ConnectedPeer> ConnectSyncAsync(byte requestMode) =>
                ConnectAsync(address, config.Port, config.Fingerprint, async (stream, token) =>
                {
                    await WriteProofAsync(stream, requestMode, config.Key, token);
                    byte[] accepted = new byte[1];
                    await stream.ReadExactlyAsync(accepted, token);
                    if (accepted[0] != requestMode) throw new AuthenticationException("LAN authentication failed.");
                }, cancellation);
        }
    }

    private static bool RejectedConnection(Exception ex) =>
        ex is IOException or InvalidDataException or SocketException or AuthenticationException or OperationCanceledException;

    private static CancellationTokenSource Timed(CancellationToken parent)
    {
        var result = CancellationTokenSource.CreateLinkedTokenSource(parent);
        result.CancelAfter(HandshakeTimeout);
        return result;
    }

    private static TcpListener Listen(IPAddress address, int port)
    {
        var listener = new TcpListener(address, port) { ExclusiveAddressUse = true };
        try { listener.Start(4); return listener; }
        catch { listener.Stop(); throw; }
    }

    private static void ValidateRemote(TcpClient peer, IPAddress local)
    {
        var remote = ((IPEndPoint)peer.Client.RemoteEndPoint!).Address;
        if (!LanNetwork.Interfaces().Any(adapter => adapter.Address.Equals(local) && LanNetwork.IsOnSubnet(remote, adapter)))
            throw new LanPeerOffSubnetException();
    }

    private static async Task<ConnectedPeer> ConnectAsync(IPAddress address, int port, byte[] pin,
        Func<SslStream, CancellationToken, Task> authenticate, CancellationToken cancellation)
    {
        var routes = LanNetwork.RoutesTo(address, LanNetwork.Interfaces());
        if (routes.Count == 0) throw new LanPeerOffSubnetException();
        Exception? failure = null;
        foreach (var local in routes)
        {
            cancellation.ThrowIfCancellationRequested();
            using var timeout = Timed(cancellation);
            TcpClient? socket = null;
            SslStream? stream = null;
            try
            {
                socket = new TcpClient(new IPEndPoint(local, 0)) { NoDelay = true };
                await socket.ConnectAsync(address, port, timeout.Token);
                stream = await SecureClientAsync(socket, pin, timeout.Token);
                await authenticate(stream, timeout.Token);
                var result = new ConnectedPeer(socket, stream);
                socket = null;
                stream = null;
                return result;
            }
            catch (Exception ex) when (RejectedConnection(ex) && !cancellation.IsCancellationRequested)
            {
                failure = ex;
            }
            finally
            {
                stream?.Dispose();
                socket?.Dispose();
            }
        }
        throw new IOException("Could not connect over a local interface.", failure);
    }

    private sealed class ConnectedPeer(TcpClient socket, SslStream stream) : IDisposable
    {
        public SslStream Stream => stream;

        public void Dispose()
        {
            stream.Dispose();
            socket.Dispose();
        }
    }

    private sealed class NetworkLifetime : IDisposable
    {
        private readonly CancellationTokenSource _stop;

        public NetworkLifetime(CancellationToken parent)
        {
            _stop = CancellationTokenSource.CreateLinkedTokenSource(parent);
            NetworkChange.NetworkAddressChanged += AddressChanged;
        }

        public CancellationToken Token => _stop.Token;

        private void AddressChanged(object? sender, EventArgs args)
        {
            try { _stop.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        public void Dispose()
        {
            NetworkChange.NetworkAddressChanged -= AddressChanged;
            _stop.Dispose();
        }
    }

    private static async Task<SslStream> SecureServerAsync(TcpClient peer, X509Certificate2 cert, CancellationToken ct)
    {
        var stream = new SslStream(peer.GetStream());
        try
        {
            await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = cert,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            }, ct);
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    private static async Task<SslStream> SecureClientAsync(TcpClient peer, byte[] pin, CancellationToken ct)
    {
        var stream = new SslStream(peer.GetStream(), false, (_, cert, _, _) =>
            cert is not null && CryptographicOperations.FixedTimeEquals(cert.GetCertHash(HashAlgorithmName.SHA256), pin));
        try
        {
            await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "BrimDeck LAN",
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            }, ct);
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    private static async Task WriteProofAsync(Stream stream, byte mode, ReadOnlyMemory<byte> secret, CancellationToken ct)
    {
        await stream.WriteAsync(new[] { mode }, ct);
        await stream.WriteAsync(secret, ct);
    }

    private static async Task<byte> ReadProofAsync(Stream stream, byte mode, byte[] expected, CancellationToken ct, bool allowImages = false)
    {
        byte[] proof = new byte[1 + KeySize];
        await stream.ReadExactlyAsync(proof, ct);
        byte requestedMode = proof[0];
        bool accepted = (requestedMode == mode || allowImages && requestedMode == SyncWithImages) &&
            CryptographicOperations.FixedTimeEquals(proof.AsSpan(1), expected);
        CryptographicOperations.ZeroMemory(proof);
        if (!accepted) throw new AuthenticationException("LAN authentication failed.");
        return requestedMode;
    }

    public static async Task WriteTextAsync(Stream stream, string text, CancellationToken ct)
    {
        var bytes = SyncTransport.TextBytes(text);
        byte[] length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        await stream.WriteAsync(length, ct);
        await stream.WriteAsync(bytes, ct);
    }

    public static async Task WriteImageAsync(Stream stream, LanPng image, CancellationToken ct)
    {
        if (!LanPng.Fits(image.Width, image.Height, image.Bytes.Length))
            throw new InvalidDataException("Invalid LAN image size.");
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, -image.Bytes.Length);
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(image.Bytes, ct);
    }

    public static async Task<string?> ReadTextAsync(Stream stream, CancellationToken ct) =>
        (await ReadContentAsync(stream, false, ct) as LanClipboardContent.Text)?.Value;

    public static async Task<LanClipboardContent?> ReadContentAsync(Stream stream, bool allowImages, CancellationToken ct)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, ct);
        int length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length == 0) return null; // Keepalive
        if (length < 0 && allowImages)
        {
            if (length < -LanPng.MaxBytes) throw new InvalidDataException("Invalid LAN image frame length.");
            byte[] png = new byte[-length];
            await stream.ReadExactlyAsync(png, ct);
            return new LanClipboardContent.Image(await Task.Run(() => LanPng.Validate(png, ct), ct));
        }
        if (length is < 0 or > SyncTransport.MaxTextBytes) throw new InvalidDataException("Invalid LAN clipboard frame length.");
        byte[] bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, ct);
        try { return new LanClipboardContent.Text(SyncTransport.Utf8.GetString(bytes)); }
        catch (DecoderFallbackException ex) { throw new InvalidDataException("Invalid UTF-8 in LAN clipboard frame.", ex); }
    }

    public static ValueTask KeepAliveAsync(Stream stream, CancellationToken ct) => stream.WriteAsync(new byte[4], ct);
}
