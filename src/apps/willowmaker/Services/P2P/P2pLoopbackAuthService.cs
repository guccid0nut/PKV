// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Hashing;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Kongor.Core.Security;

namespace WILLOWMAKER.Services.P2P;

/// <summary>
/// Stateless self-describing 16-byte (32 lowercase hex chars) P2P session cookie codec.
/// Encodes byte[0] = 0xA5 magic marker + up to 15 ASCII bytes of the player's nickname so a Host's
/// local <see cref="P2pLoopbackAuthServer"/> can decode any remote friend's exact nickname and deterministic
/// AccountID during <c>server_requester.php?f=c_conn</c> with zero extra TCP/HTTP ports exposed over WAN/LAN.
/// </summary>
public static class P2pCookieCodec
{
    public const byte MagicPrefixByte = 0xA5;
    public const int CookieByteLength = 16;
    public const int CookieHexLength = 32;
    public const int MaxEncodedNicknameBytes = 15;

    public static string SanitizeNickname(string? rawNickname)
    {
        if (string.IsNullOrWhiteSpace(rawNickname))
        {
            return "Player";
        }

        string trimmed = rawNickname.Trim();
        StringBuilder sb = new StringBuilder(MaxEncodedNicknameBytes);
        foreach (char ch in trimmed)
        {
            if (sb.Length >= MaxEncodedNicknameBytes)
            {
                break;
            }

            if (ch == ' ')
            {
                sb.Append('_');
            }
            else if (ch >= 0x21 && ch <= 0x7E && ch != '"' && ch != '\'' && ch != '\\' && ch != ':')
            {
                sb.Append(ch);
            }
        }

        return sb.Length > 0 ? sb.ToString() : "Player";
    }

    public static int ComputeDeterministicAccountId(string? nickname)
    {
        string clean = SanitizeNickname(nickname).ToLowerInvariant();
        byte[] utf8 = Encoding.UTF8.GetBytes(clean);
        uint crc = Crc32.HashToUInt32(utf8);
        return 100000 + (int)(crc % 899999u);
    }

    public static string Encode(string? nickname)
    {
        string clean = SanitizeNickname(nickname);
        Span<byte> buffer = stackalloc byte[CookieByteLength];
        buffer.Clear();
        buffer[0] = MagicPrefixByte;

        int count = Math.Min(clean.Length, MaxEncodedNicknameBytes);
        for (int i = 0; i < count; i++)
        {
            buffer[1 + i] = (byte)clean[i];
        }

        return Convert.ToHexStringLower(buffer);
    }

    public static (string Nickname, int AccountId, bool IsP2pCookie) DecodeOrFallback(string? cookie)
    {
        string cleanCookie = string.IsNullOrWhiteSpace(cookie) ? string.Empty : cookie.Trim();
        if (cleanCookie.Length == CookieHexLength && IsAsciiHex(cleanCookie))
        {
            byte[] raw = Convert.FromHexString(cleanCookie);
            if (raw.Length == CookieByteLength && raw[0] == MagicPrefixByte)
            {
                int len = 0;
                while (len < MaxEncodedNicknameBytes && raw[1 + len] != 0)
                {
                    byte b = raw[1 + len];
                    if (b < 0x21 || b > 0x7E)
                    {
                        break;
                    }

                    len++;
                }

                string decodedNick = len > 0
                    ? Encoding.ASCII.GetString(raw, 1, len)
                    : "Player";
                int decodedId = ComputeDeterministicAccountId(decodedNick);
                return (decodedNick, decodedId, true);
            }
        }

        if (string.IsNullOrEmpty(cleanCookie))
        {
            int fallbackId = ComputeDeterministicAccountId("Player");
            return ("Player", fallbackId, false);
        }

        byte[] cookieUtf8 = Encoding.UTF8.GetBytes(cleanCookie.ToLowerInvariant());
        uint hash = Crc32.HashToUInt32(cookieUtf8);
        int onlinePeerId = 100000 + (int)(hash % 899999u);
        string onlinePeerNick = $"Player_{hash % 10000u:D4}";
        return (onlinePeerNick, onlinePeerId, false);
    }

    private static bool IsAsciiHex(string value)
    {
        foreach (char ch in value)
        {
            if (!char.IsAsciiHexDigit(ch))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Process-wide singleton coordinator for the local P2P loopback HTTP auth responder.
/// </summary>
public static class P2pLoopbackAuthService
{
    public const int DefaultPreferredPort = 19999;

    private static readonly object SyncRoot = new();
    private static P2pLoopbackAuthServer? _activeServer;
    private static bool _exitHookRegistered;

    public static int ActivePort
    {
        get
        {
            lock (SyncRoot)
            {
                return _activeServer is { IsRunning: true } ? _activeServer.BoundPort : 0;
            }
        }
    }

    public static string ActiveEndpoint
    {
        get
        {
            int port = ActivePort;
            return port > 0 ? $"127.0.0.1:{port}" : $"127.0.0.1:{DefaultPreferredPort}";
        }
    }

    public static string EnsureStarted(int preferredPort = DefaultPreferredPort)
    {
        lock (SyncRoot)
        {
            if (_activeServer is { IsRunning: true })
            {
                return _activeServer.Endpoint;
            }

            _activeServer?.Dispose();
            _activeServer = P2pLoopbackAuthServer.StartOnAvailableLoopbackPort(preferredPort);

            if (!_exitHookRegistered)
            {
                _exitHookRegistered = true;
                AppDomain.CurrentDomain.ProcessExit += static (_, _) => Stop();
            }

            Trace.WriteLine($"[P2pLoopbackAuth] Active loopback responder bound on http://{_activeServer.Endpoint}/ (preferred={preferredPort}).");
            return _activeServer.Endpoint;
        }
    }

    public static void Stop()
    {
        lock (SyncRoot)
        {
            if (_activeServer is null)
            {
                return;
            }

            Trace.WriteLine($"[P2pLoopbackAuth] Stopping loopback responder on {_activeServer.Endpoint}.");
            _activeServer.Dispose();
            _activeServer = null;
        }
    }
}

/// <summary>
/// Cross-platform, AOT-safe loopback HTTP responder (<c>127.0.0.1:&lt;port&gt;</c>) implementing the minimal
/// PHP wire endpoints required by retail K2 clients (<c>login_useSRP=false</c> -&gt; <c>client_requester.php?f=auth</c>)
/// and companion <c>server_p2p</c> dedicated hosts (<c>server_requester.php?f=new_session</c>, <c>c_conn</c>, <c>start_game</c>).
/// </summary>
public sealed class P2pLoopbackAuthServer : IDisposable
{
    private const string ServerSessionCookie = "a55032505345525645525f5345535331";
    private const int MaxRequestHeaderBytes = 32 * 1024;
    private const int MaxRequestBodyBytes = 512 * 1024;
    private const ushort NetChatGsConnect = 0x0500;
    private const ushort NetChatGsAccept = 0x1500;
    private const ushort NetChatPing = 0x2A00;
    private const ushort NetChatPong = 0x2A01;
    private const int MaxGsChatFrameBytes = 16 * 1024;

    private readonly TcpListener _listener;
    private readonly TcpListener _gsChatListener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoopTask;
    private readonly Task _gsChatAcceptLoopTask;
    private readonly ConcurrentDictionary<int, string> _knownAccountCookies = new();
    private int _nextMatchId = 1000;
    private volatile int _lastServerPort;
    private volatile string _lastServerName = "P2P_Host";
    private volatile bool _disposed;

    private P2pLoopbackAuthServer(TcpListener listener, int boundPort, TcpListener gsChatListener, int gsChatPort)
    {
        _listener = listener;
        _gsChatListener = gsChatListener;
        BoundPort = boundPort;
        GsChatPort = gsChatPort;
        Endpoint = $"127.0.0.1:{boundPort}";
        _acceptLoopTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
        _gsChatAcceptLoopTask = Task.Run(() => GsChatAcceptLoopAsync(_cts.Token));
    }

    public int BoundPort { get; }

    public int GsChatPort { get; }

    public string Endpoint { get; }

    public bool IsRunning => !_disposed;

    public static P2pLoopbackAuthServer StartOnAvailableLoopbackPort(int preferredPort = P2pLoopbackAuthService.DefaultPreferredPort, int maxPortProbeOffset = 32)
    {
        if (preferredPort > 0 && preferredPort <= 65535)
        {
            int maxOffset = Math.Max(0, maxPortProbeOffset);
            for (int offset = 0; offset <= maxOffset; offset++)
            {
                int candidate = preferredPort + offset;
                if (candidate > 65535)
                {
                    break;
                }

                TcpListener? candidateListener = null;
                try
                {
                    candidateListener = new TcpListener(IPAddress.Loopback, candidate);
                    candidateListener.Server.ExclusiveAddressUse = true;
                    candidateListener.Start();
                    int actualPort = ((IPEndPoint)candidateListener.LocalEndpoint).Port;
                    (TcpListener gsListener, int gsPort) = StartGsChatListener();
                    Trace.WriteLine($"[P2pLoopbackAuth] Bound HTTP 127.0.0.1:{actualPort} + GS Chat TCP 127.0.0.1:{gsPort}.");
                    return new P2pLoopbackAuthServer(candidateListener, actualPort, gsListener, gsPort);
                }
                catch (SocketException ex)
                {
                    candidateListener?.Stop();
                    Trace.WriteLine($"[P2pLoopbackAuth] Loopback TCP port {candidate} busy ({ex.SocketErrorCode}); probing next port.");
                }
            }
        }

        TcpListener ephemeralListener = new TcpListener(IPAddress.Loopback, 0);
        ephemeralListener.Start();
        int ephemeralPort = ((IPEndPoint)ephemeralListener.LocalEndpoint).Port;
        (TcpListener ephemeralGsListener, int ephemeralGsPort) = StartGsChatListener();
        Trace.WriteLine($"[P2pLoopbackAuth] Bound ephemeral HTTP 127.0.0.1:{ephemeralPort} + GS Chat TCP 127.0.0.1:{ephemeralGsPort}.");
        return new P2pLoopbackAuthServer(ephemeralListener, ephemeralPort, ephemeralGsListener, ephemeralGsPort);
    }

    private static (TcpListener Listener, int Port) StartGsChatListener()
    {
        TcpListener gsListener = new TcpListener(IPAddress.Loopback, 0);
        gsListener.Start();
        int gsPort = ((IPEndPoint)gsListener.LocalEndpoint).Port;
        return (gsListener, gsPort);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            _listener.Stop();
        }
        catch (SocketException ex)
        {
            Trace.WriteLine($"[P2pLoopbackAuth] Listener stop notice: {ex.Message}");
        }

        try
        {
            _gsChatListener.Stop();
        }
        catch (SocketException ex)
        {
            Trace.WriteLine($"[P2pLoopbackAuth] GS Chat listener stop notice: {ex.Message}");
        }

        _cts.Dispose();
    }

    private async Task GsChatAcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && !_disposed)
        {
            TcpClient? gsClient = null;
            try
            {
                gsClient = await _gsChatListener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                _ = Task.Run(() => HandleGsChatClientAsync(gsClient, cancellationToken), CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                gsClient?.Dispose();
                break;
            }
            catch (ObjectDisposedException)
            {
                gsClient?.Dispose();
                break;
            }
            catch (SocketException ex)
            {
                gsClient?.Dispose();
                if (_disposed || cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                Trace.WriteLine($"[P2pLoopbackAuth] GS Chat accept warning on 127.0.0.1:{GsChatPort}: {ex.Message}");
            }
            catch (Exception ex)
            {
                gsClient?.Dispose();
                Trace.WriteLine($"[P2pLoopbackAuth] GS Chat unexpected accept warning on 127.0.0.1:{GsChatPort}: {ex.Message}");
            }
        }
    }

    private async Task HandleGsChatClientAsync(TcpClient gsClient, CancellationToken cancellationToken)
    {
        using TcpClient ownedClient = gsClient;
        ownedClient.NoDelay = true;

        try
        {
            await using NetworkStream stream = ownedClient.GetStream();
            byte[] headerBuf = new byte[2];
            byte[] replyBuf = new byte[4];

            while (!cancellationToken.IsCancellationRequested && !_disposed)
            {
                bool readHeader = await ReadExactAsync(stream, headerBuf, 2, cancellationToken).ConfigureAwait(false);
                if (!readHeader)
                {
                    break;
                }

                ushort payloadLen = BinaryPrimitives.ReadUInt16LittleEndian(headerBuf);
                if (payloadLen > MaxGsChatFrameBytes)
                {
                    Trace.WriteLine($"[P2pLoopbackAuth] GS Chat framing violation (len={payloadLen}) on 127.0.0.1:{GsChatPort}; closing socket.");
                    break;
                }

                if (payloadLen == 0)
                {
                    continue;
                }

                byte[] payloadBuf = new byte[payloadLen];
                bool readPayload = await ReadExactAsync(stream, payloadBuf, payloadLen, cancellationToken).ConfigureAwait(false);
                if (!readPayload)
                {
                    break;
                }

                if (payloadLen < 2)
                {
                    continue;
                }

                ushort opcode = BinaryPrimitives.ReadUInt16LittleEndian(payloadBuf.AsSpan(0, 2));
                Trace.WriteLine($"[P2pLoopbackAuth] GS Chat TCP frame opcode=0x{opcode:X4} len={payloadLen} on 127.0.0.1:{GsChatPort}.");

                if (opcode == NetChatGsConnect)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(replyBuf.AsSpan(0, 2), 2);
                    BinaryPrimitives.WriteUInt16LittleEndian(replyBuf.AsSpan(2, 2), NetChatGsAccept);
                    await stream.WriteAsync(replyBuf, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    Trace.WriteLine($"[P2pLoopbackAuth] GS Chat TCP accepted NET_CHAT_GS_CONNECT (0x0500) -> NET_CHAT_GS_ACCEPT (0x1500) on 127.0.0.1:{GsChatPort}.");
                }
                else if (opcode == NetChatPing)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(replyBuf.AsSpan(0, 2), 2);
                    BinaryPrimitives.WriteUInt16LittleEndian(replyBuf.AsSpan(2, 2), NetChatPong);
                    await stream.WriteAsync(replyBuf, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            Trace.WriteLine($"[P2pLoopbackAuth] GS Chat connection closed on 127.0.0.1:{GsChatPort}: {ex.Message}");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[P2pLoopbackAuth] GS Chat unexpected error on 127.0.0.1:{GsChatPort}: {ex}");
        }
    }

    private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, int count, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < count)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), cancellationToken).ConfigureAwait(false);
            if (n <= 0)
            {
                return false;
            }

            offset += n;
        }

        return true;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && !_disposed)
        {
            TcpClient? client = null;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                _ = Task.Run(() => HandleClientAsync(client, cancellationToken), CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                client?.Dispose();
                break;
            }
            catch (ObjectDisposedException)
            {
                client?.Dispose();
                break;
            }
            catch (SocketException ex)
            {
                client?.Dispose();
                if (_disposed || cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                Trace.WriteLine($"[P2pLoopbackAuth] Accept socket warning on {Endpoint}: {ex.Message}");
            }
            catch (Exception ex)
            {
                client?.Dispose();
                Trace.WriteLine($"[P2pLoopbackAuth] Unexpected accept warning on {Endpoint}: {ex.Message}");
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        Stopwatch sw = Stopwatch.StartNew();
        using TcpClient ownedClient = client;
        ownedClient.NoDelay = true;
        ownedClient.ReceiveTimeout = 5000;
        ownedClient.SendTimeout = 5000;

        try
        {
            using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
            CancellationToken token = timeoutCts.Token;

            await using NetworkStream stream = ownedClient.GetStream();
            ParsedHttpRequest? request = await ReadHttpRequestAsync(stream, token).ConfigureAwait(false);
            if (request is null)
            {
                return;
            }

            byte[] responseBody = RouteRequest(request, out string contentType, out string summaryTag);
            string responseHeader =
                "HTTP/1.1 200 OK\r\n" +
                $"Content-Type: {contentType}\r\n" +
                $"Content-Length: {responseBody.Length.ToString(CultureInfo.InvariantCulture)}\r\n" +
                "Connection: close\r\n" +
                "\r\n";

            byte[] headerBytes = Encoding.ASCII.GetBytes(responseHeader);
            await stream.WriteAsync(headerBytes, token).ConfigureAwait(false);
            if (!request.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase) && responseBody.Length > 0)
            {
                await stream.WriteAsync(responseBody, token).ConfigureAwait(false);
            }

            await stream.FlushAsync(token).ConfigureAwait(false);
            Trace.WriteLine(
                $"[P2pLoopbackAuth] {request.Method} {request.Path} ({summaryTag}) -> 200 OK ({responseBody.Length} bytes) in {sw.Elapsed.TotalMilliseconds:F2} ms.");
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            Trace.WriteLine($"[P2pLoopbackAuth] Connection closed/aborted on {Endpoint}: {ex.Message}");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[P2pLoopbackAuth] Error handling request on {Endpoint}: {ex}");
            try
            {
                if (ownedClient.Connected)
                {
                    NetworkStream errStream = ownedClient.GetStream();
                    byte[] errBody = Encoding.UTF8.GetBytes($"[P2pLoopbackAuth Error] {ex.GetType().Name}: {ex.Message}");
                    string errHeaders =
                        "HTTP/1.1 500 Internal Server Error\r\n" +
                        "Content-Type: text/plain; charset=utf-8\r\n" +
                        $"Content-Length: {errBody.Length.ToString(CultureInfo.InvariantCulture)}\r\n" +
                        "Connection: close\r\n\r\n";
                    byte[] errHeaderBytes = Encoding.ASCII.GetBytes(errHeaders);
                    await errStream.WriteAsync(errHeaderBytes, CancellationToken.None).ConfigureAwait(false);
                    await errStream.WriteAsync(errBody, CancellationToken.None).ConfigureAwait(false);
                    await errStream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception sendErrEx)
            {
                Trace.WriteLine($"[P2pLoopbackAuth] Secondary error sending 500 response: {sendErrEx.Message}");
            }
        }
    }

    private byte[] RouteRequest(ParsedHttpRequest request, out string contentType, out string summaryTag)
    {
        contentType = "text/html; charset=utf-8";
        string pathLower = request.Path.ToLowerInvariant();
        string func = GetParam(request.Parameters, "f").ToLowerInvariant();

        if (pathLower.EndsWith("/client_requester.php", StringComparison.Ordinal) ||
            pathLower.Equals("client_requester.php", StringComparison.Ordinal))
        {
            return HandleClientRequester(func, request.Parameters, out contentType, out summaryTag);
        }

        if (pathLower.EndsWith("/server_requester.php", StringComparison.Ordinal) ||
            pathLower.Equals("server_requester.php", StringComparison.Ordinal))
        {
            return HandleServerRequester(func, request.Parameters, out summaryTag);
        }

        if (pathLower.EndsWith("/patcher.php", StringComparison.Ordinal) ||
            pathLower.Equals("patcher.php", StringComparison.Ordinal))
        {
            return HandlePatcherRequester(request.Parameters, out summaryTag);
        }

        summaryTag = $"fallback:{pathLower}";
        return Encoding.UTF8.GetBytes("a:0:{}");
    }

    private byte[] HandlePatcherRequester(
        Dictionary<string, List<string>> parameters,
        out string summaryTag)
    {
        string os = GetParam(parameters, "os");
        if (string.IsNullOrWhiteSpace(os))
        {
            os = "wac";
        }

        string arch = GetParam(parameters, "arch");
        if (string.IsNullOrWhiteSpace(arch))
        {
            arch = "x86_64";
        }

        string currentVersion = GetParam(parameters, "current_version");
        if (string.IsNullOrWhiteSpace(currentVersion))
        {
            string reqVer = GetParam(parameters, "version");
            currentVersion = !string.IsNullOrWhiteSpace(reqVer) && reqVer != "0.0.0.0"
                ? reqVer
                : "5.0.0.0";
        }

        string[] parts = currentVersion.Split('.');
        string shortVersion = parts.Length == 4 && parts[3] == "0"
            ? $"{parts[0]}.{parts[1]}.{parts[2]}"
            : currentVersion;

        string cdnUrl = $"http://{Endpoint}/";
        summaryTag = $"patcher:os={os}:ver={currentVersion}:latest={shortVersion}";

        PhpWireBuilder w = new PhpWireBuilder();
        w.BeginArray(5);

        w.WriteKey(0);
        w.BeginArray(8);
        w.WriteStringEntry("version", shortVersion);
        w.WriteStringEntry("latest_version", shortVersion);
        w.WriteStringEntry("os", os);
        w.WriteStringEntry("arch", arch);
        w.WriteStringEntry("url", cdnUrl);
        w.WriteStringEntry("url2", cdnUrl);
        w.WriteStringEntry("latest_manifest_checksum", string.Empty);
        w.WriteStringEntry("latest_manifest_size", "0");
        w.EndArray();

        w.WriteStringEntry("version", currentVersion);
        w.WriteStringEntry("current_version", currentVersion);
        w.WriteStringEntry("current_manifest_checksum", string.Empty);
        w.WriteStringEntry("current_manifest_size", "0");
        w.EndArray();

        return Encoding.UTF8.GetBytes(w.ToString());
    }

    private byte[] HandleClientRequester(
        string func,
        Dictionary<string, List<string>> parameters,
        out string contentType,
        out string summaryTag)
    {
        contentType = "text/html; charset=utf-8";

        if (func == "ping")
        {
            contentType = "text/plain; charset=utf-8";
            summaryTag = "client:ping";
            return Encoding.UTF8.GetBytes("pong");
        }

        if (func is "auth" or "cookie_login" or "autoauth")
        {
            string rawLogin = GetParam(parameters, "login");
            if (string.IsNullOrWhiteSpace(rawLogin))
            {
                string existingCookie = GetParam(parameters, "cookie");
                (string decodedNick, _, _) = P2pCookieCodec.DecodeOrFallback(existingCookie);
                rawLogin = decodedNick;
            }

            string cleanNick = P2pCookieCodec.SanitizeNickname(rawLogin);
            int accountId = P2pCookieCodec.ComputeDeterministicAccountId(cleanNick);
            string cookie = P2pCookieCodec.Encode(cleanNick);
            _knownAccountCookies[accountId] = cookie;

            summaryTag = $"client:{func}:nick={cleanNick}:aid={accountId}";
            string phpPayload = BuildClientAuthPhpPayload(cleanNick, accountId, cookie);
            return Encoding.UTF8.GetBytes(phpPayload);
        }

        if (func == "logout")
        {
            summaryTag = "client:logout";
            return Encoding.UTF8.GetBytes("b:1;");
        }

        if (func == "server_list")
        {
            string rawCookie = GetParam(parameters, "cookie");
            (string decodedNick, _, _) = P2pCookieCodec.DecodeOrFallback(rawCookie);
            string resolvedCookie = string.IsNullOrWhiteSpace(rawCookie)
                ? P2pCookieCodec.Encode(decodedNick)
                : rawCookie.Trim();

            string gameType = GetParam(parameters, "gametype");
            if (string.IsNullOrWhiteSpace(gameType))
            {
                gameType = GetParam(parameters, "game_type");
            }

            string accKey = Guid.CreateVersion7().ToString();
            string accKeyHash = S2Crypto.ComputeMatchServerChatAuthenticationHash(accKey, resolvedCookie);
            int activePort = _lastServerPort;
            string activeName = _lastServerName;

            PhpWireBuilder w = new PhpWireBuilder();
            w.BeginArray(5);
            w.WriteKey(0);
            w.WriteBoolValue(true);
            w.WriteStringEntry("acc_key", accKey);
            w.WriteStringEntry("acc_key_hash", accKeyHash);
            w.WriteIntEntry("vested_threshold", 5);
            w.WriteKey("server_list");
            if (activePort > 0)
            {
                w.BeginArray(1);
                w.WriteKey(0);
                if (gameType == "90")
                {
                    w.BeginArray(7);
                    w.WriteStringEntry("server_id", "1");
                    w.WriteStringEntry("ip", "127.0.0.1");
                    w.WriteStringEntry("port", activePort.ToString(CultureInfo.InvariantCulture));
                    w.WriteStringEntry("location", "LOCAL");
                    w.WriteStringEntry("name", activeName);
                    w.WriteStringEntry("c_state", "1");
                    w.WriteStringEntry("class", "1");
                    w.EndArray();
                }
                else
                {
                    w.BeginArray(6);
                    w.WriteStringEntry("server_id", "1");
                    w.WriteStringEntry("ip", "127.0.0.1");
                    w.WriteStringEntry("port", activePort.ToString(CultureInfo.InvariantCulture));
                    w.WriteStringEntry("location", "LOCAL");
                    w.WriteStringEntry("name", activeName);
                    w.WriteStringEntry("class", "1");
                    w.EndArray();
                }

                w.EndArray();
            }
            else
            {
                w.BeginArray(0);
                w.EndArray();
            }

            w.EndArray();
            summaryTag = $"client:server_list:type={gameType}:port={activePort}";
            return Encoding.UTF8.GetBytes(w.ToString());
        }

        summaryTag = $"client:{(string.IsNullOrEmpty(func) ? "empty" : func)}";
        return Encoding.UTF8.GetBytes("a:0:{}");
    }

    private byte[] HandleServerRequester(
        string func,
        Dictionary<string, List<string>> parameters,
        out string summaryTag)
    {
        switch (func)
        {
            case "replay_auth":
            case "new_session":
            case "auth":
            {
                string login = GetParam(parameters, "login");
                string port = GetParam(parameters, "port");
                string serverName = GetParam(parameters, "name");
                if (int.TryParse(port, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedPort) &&
                    parsedPort > 0 &&
                    parsedPort <= 65535)
                {
                    _lastServerPort = parsedPort;
                }

                if (!string.IsNullOrWhiteSpace(serverName))
                {
                    _lastServerName = serverName.Trim();
                }

                summaryTag = $"server:{func}:login={login}:port={port}:gsChat={GsChatPort}";

                PhpWireBuilder w = new PhpWireBuilder();
                w.BeginArray(5);
                w.WriteStringEntry("session", ServerSessionCookie);
                w.WriteIntEntry("server_id", 1);
                w.WriteStringEntry("chat_address", "127.0.0.1");
                w.WriteIntEntry("chat_port", GsChatPort);
                w.WriteDoubleEntry("leaverthreshold", 0.05);
                w.EndArray();
                return Encoding.UTF8.GetBytes(w.ToString());
            }

            case "c_conn":
            {
                string rawCookie = GetParam(parameters, "cookie");
                (string nickname, int accountId, bool isP2p) = P2pCookieCodec.DecodeOrFallback(rawCookie);
                string resolvedCookie = string.IsNullOrWhiteSpace(rawCookie)
                    ? P2pCookieCodec.Encode(nickname)
                    : rawCookie.Trim();

                _knownAccountCookies[accountId] = resolvedCookie;
                summaryTag = $"server:c_conn:nick={nickname}:aid={accountId}:p2p={isP2p}";

                PhpWireBuilder w = new PhpWireBuilder();
                w.BeginArray(14);
                w.WriteStringEntry("cookie", resolvedCookie);
                w.WriteIntEntry("account_id", accountId);
                w.WriteStringEntry("nickname", nickname);
                w.WriteIntEntry("super_id", accountId);
                w.WriteIntEntry("account_type", 3);
                w.WriteIntEntry("level", 100);
                w.WriteStringEntry("infos", string.Empty);
                w.WriteStringEntry("game_cookie", resolvedCookie);
                w.WriteStringEntry("standing", "3");
                w.WriteStringEntry("is_legacy", "1");
                w.WriteIntEntry("game_tokens", 999999);
                w.WriteIntEntry("dice_tokens", 999999);

                w.WriteKey("my_upgrades");
                w.BeginArray(1);
                w.WriteIndexedString(0, "m.allmodes.pass");
                w.EndArray();

                w.WriteKey("selected_upgrades");
                w.BeginArray(0);
                w.EndArray();

                w.EndArray();
                return Encoding.UTF8.GetBytes(w.ToString());
            }

            case "start_game":
            {
                int matchId = Interlocked.Increment(ref _nextMatchId);
                string map = GetParam(parameters, "map");
                if (string.IsNullOrWhiteSpace(map))
                {
                    map = "caldavar";
                }

                string nowStr = TimeProvider.System.GetUtcNow().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                summaryTag = $"server:start_game:matchId={matchId}:map={map}";

                PhpWireBuilder w = new PhpWireBuilder();
                w.BeginArray(8);
                w.WriteIntEntry("match_id", matchId);
                w.WriteStringEntry("match_date", nowStr);
                w.WriteStringEntry("file_host", Endpoint);
                w.WriteBoolEntry("is_recommended", false);
                w.WriteStringEntry("soccer_hero_list", string.Empty);
                w.WriteStringEntry("free_hero_list", string.Empty);
                w.WriteStringEntry("early_access_hero_list", string.Empty);
                w.WriteStringEntry("disabled_hero_list", string.Empty);
                w.EndArray();
                return Encoding.UTF8.GetBytes(w.ToString());
            }

            case "accept_key":
            {
                summaryTag = "server:accept_key";
                PhpWireBuilder w = new PhpWireBuilder();
                w.BeginArray(2);
                w.WriteIntEntry("server_id", 1);
                w.WriteIntEntry("official", 0);
                w.EndArray();
                return Encoding.UTF8.GetBytes(w.ToString());
            }

            case "aids2cookie":
            {
                List<int> aids = ExtractAccountIds(parameters);
                summaryTag = $"server:aids2cookie:count={aids.Count}";

                PhpWireBuilder w = new PhpWireBuilder();
                w.BeginArray(2);

                w.WriteKey("aids2cookie");
                w.BeginArray(aids.Count);
                foreach (int aid in aids)
                {
                    string c = _knownAccountCookies.TryGetValue(aid, out string? known) && !string.IsNullOrEmpty(known)
                        ? known
                        : P2pCookieCodec.Encode($"Peer_{aid}");
                    w.WriteKey(aid);
                    w.WriteStringValue(c);
                }
                w.EndArray();

                w.WriteKey("aids2gcookie");
                w.BeginArray(aids.Count);
                foreach (int aid in aids)
                {
                    string c = _knownAccountCookies.TryGetValue(aid, out string? known) && !string.IsNullOrEmpty(known)
                        ? known
                        : P2pCookieCodec.Encode($"Peer_{aid}");
                    w.WriteKey(aid);
                    w.WriteStringValue(c);
                }
                w.EndArray();

                w.EndArray();
                return Encoding.UTF8.GetBytes(w.ToString());
            }

            case "get_upgrades":
            {
                summaryTag = "server:get_upgrades";
                return Encoding.UTF8.GetBytes("a:0:{}");
            }

            case "set_online":
            case "shutdown":
            case "abandon_match":
            case "cancel_match":
            case "live_tick":
            case "match_telemetry":
            case "set_replay_size":
            case "get_spectator_header":
            case "get_quickstats":
            {
                string cState = GetParam(parameters, "c_state");
                string numConn = GetParam(parameters, "num_conn");
                summaryTag = $"server:{func}:state={cState}:conns={numConn}";
                return Encoding.UTF8.GetBytes("b:1;");
            }

            default:
            {
                summaryTag = $"server:unsupported:{(string.IsNullOrEmpty(func) ? "empty" : func)}";
                return Encoding.UTF8.GetBytes("b:0;");
            }
        }
    }

    private string BuildClientAuthPhpPayload(string nickname, int accountId, string cookie)
    {
        string accountIdStr = accountId.ToString(CultureInfo.InvariantCulture);
        DateTimeOffset nowUtc = TimeProvider.System.GetUtcNow();
        long nowUnix = nowUtc.ToUnixTimeSeconds();
        string nowUnixStr = nowUnix.ToString(CultureInfo.InvariantCulture);
        string nowDateStr = nowUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        string authHash = S2Crypto.ComputeChatServerCookieHash(accountId, "127.0.0.1", cookie);

        PhpWireBuilder w = new PhpWireBuilder();
        w.BeginArray(56);

        w.WriteKey(0);
        w.WriteBoolValue(true);

        w.WriteStringEntry("proof", string.Empty);
        w.WriteStringEntry("super_id", accountIdStr);
        w.WriteStringEntry("account_id", accountIdStr);
        w.WriteStringEntry("garena_id", "0");
        w.WriteStringEntry("nickname", nickname);
        w.WriteStringEntry("email", "p2p@localhost");
        w.WriteStringEntry("account_type", "3");
        w.WriteStringEntry("trial", "0");
        w.WriteStringEntry("susp_id", "0");
        w.WriteStringEntry("prepay_only", "0");
        w.WriteStringEntry("standing", "3");
        w.WriteStringEntry("use_cloud", "0");
        w.WriteNullEntry("pass_exp");
        w.WriteIntEntry("is_new", 0);
        w.WriteStringEntry("cookie", cookie);
        w.WriteStringEntry("ip", "127.0.0.1");
        w.WriteStringEntry("minimum_ranked_level", "1");
        w.WriteStringEntry("leaverthreshold", ".20");
        w.WriteBoolEntry("is_under_24", false);
        w.WriteBoolEntry("is_subaccount", false);
        w.WriteBoolEntry("is_current_subaccount", false);
        w.WriteStringEntry("icb_url", $"http://{Endpoint}");
        w.WriteStringEntry("auth_hash", authHash);
        w.WriteStringEntry("host_time", nowUnixStr);
        w.WriteStringEntry("chat_url", "127.0.0.1");
        w.WriteStringEntry("chat_port", "19998");

        w.WriteKey("chatrooms");
        w.BeginArray(0);
        w.EndArray();

        w.WriteKey("identities");
        w.BeginArray(1);
        w.WriteKey(0);
        w.BeginArray(2);
        w.WriteIndexedString(0, nickname);
        w.WriteIndexedString(1, accountIdStr);
        w.EndArray();
        w.EndArray();

        w.WriteStringEntry("points", "999999");
        w.WriteIntEntry("mmpoints", 999999);
        w.WriteIntEntry("dice_tokens", 999999);
        w.WriteIntEntry("game_tokens", 999999);
        w.WriteIntEntry("season_level", 100);
        w.WriteIntEntry("creep_level", 100);
        w.WriteStringEntry("slot_id", "0");
        w.WriteLongEntry("timestamp", nowUnix);
        w.WriteStringEntry("campaign_current_season", "12");
        w.WriteStringEntry(
            "mmr_rank",
            "1250,1275,1300,1330,1365,1400,1435,1470,1505,1540,1575,1610,1645,1685,1725,1765,1805,1850,1900,1950");
        w.WriteIntEntry("mute_expiration", 0);
        w.WriteIntEntry("vested_threshold", 5);

        w.WriteKey("buddy_list");
        w.BeginArray(1);
        w.WriteKey(accountIdStr);
        w.BeginArray(0);
        w.EndArray();
        w.EndArray();

        w.WriteKey("ignored_list");
        w.BeginArray(1);
        w.WriteKey(accountIdStr);
        w.BeginArray(0);
        w.EndArray();
        w.EndArray();

        w.WriteKey("banned_list");
        w.BeginArray(1);
        w.WriteKey(accountIdStr);
        w.BeginArray(0);
        w.EndArray();
        w.EndArray();

        w.WriteKey("clan_roster");
        w.BeginArray(0);
        w.EndArray();

        w.WriteKey("clan_member_info");
        w.BeginArray(1);
        w.WriteStringEntry("error", "No Clan Member Found");
        w.EndArray();

        w.WriteKey("my_upgrades");
        w.BeginArray(1);
        w.WriteIndexedString(0, "m.allmodes.pass");
        w.EndArray();

        w.WriteKey("selected_upgrades");
        w.BeginArray(0);
        w.EndArray();

        w.WriteKey("my_upgrades_info");
        w.BeginArray(0);
        w.EndArray();

        w.WriteKey("hero_list");
        w.BeginArray(1);
        w.WriteStringEntry("free", "Hero_Genesis,Hero_Dorin_Tal,Hero_Adeve");
        w.EndArray();

        w.WriteKey("sec_info");
        w.BeginArray(3);
        w.WriteStringEntry("initial_vector", "73088db5e71cfb6d");
        w.WriteStringEntry("hash_code", "73088db5e71cfb6d43ae0bb4abf095dd43862200");
        w.WriteStringEntry("key_version", "3e2d");
        w.EndArray();

        w.WriteKey("awards_tooltip");
        w.BeginArray(0);
        w.EndArray();

        w.WriteKey("infos");
        w.BeginArray(1);
        w.WriteKey(0);
        w.BeginArray(34);
        w.WriteStringEntry("account_id", accountIdStr);
        w.WriteStringEntry("standing", "3");
        w.WriteStringEntry("level", "100");
        w.WriteStringEntry("level_exp", "0");
        w.WriteStringEntry("discos", "0");
        w.WriteStringEntry("possible_discos", "0");
        w.WriteStringEntry("games_played", "0");
        w.WriteStringEntry("num_bot_games_won", "0");
        w.WriteStringEntry("acc_pub_skill", "1500.000");
        w.WriteStringEntry("acc_wins", "0");
        w.WriteStringEntry("acc_losses", "0");
        w.WriteStringEntry("acc_games_played", "0");
        w.WriteStringEntry("acc_discos", "0");
        w.WriteStringEntry("rnk_amm_team_rating", "1500.000");
        w.WriteStringEntry("rnk_wins", "0");
        w.WriteStringEntry("rnk_losses", "0");
        w.WriteStringEntry("rnk_games_played", "0");
        w.WriteStringEntry("rnk_discos", "0");
        w.WriteStringEntry("cs_amm_team_rating", "1500.000");
        w.WriteStringEntry("cs_wins", "0");
        w.WriteStringEntry("cs_losses", "0");
        w.WriteStringEntry("cs_games_played", "0");
        w.WriteStringEntry("cs_discos", "0");
        w.WriteStringEntry("mid_amm_team_rating", "1500.000");
        w.WriteStringEntry("mid_games_played", "0");
        w.WriteStringEntry("mid_discos", "0");
        w.WriteStringEntry("rift_amm_team_rating", "1500.000");
        w.WriteStringEntry("rift_games_played", "0");
        w.WriteStringEntry("rift_discos", "0");
        w.WriteIntEntry("cam_games_played", 0);
        w.WriteIntEntry("cam_discos", 0);
        w.WriteIntEntry("cam_cs_games_played", 0);
        w.WriteIntEntry("cam_cs_discos", 0);
        w.WriteIntEntry("is_new", 0);
        w.EndArray();
        w.EndArray();

        w.WriteKey("quest_system");
        w.BeginArray(1);
        w.WriteKey("error");
        w.BeginArray(2);
        w.WriteIntEntry("quest_status", 0);
        w.WriteIntEntry("leaderboard_status", 0);
        w.EndArray();
        w.EndArray();

        w.WriteKey("account_cloud_storage_info");
        w.BeginArray(4);
        w.WriteStringEntry("account_id", accountIdStr);
        w.WriteStringEntry("use_cloud", "0");
        w.WriteStringEntry("cloud_autoupload", "0");
        w.WriteStringEntry("file_modify_time", nowDateStr);
        w.EndArray();

        w.WriteKey("notifications");
        w.BeginArray(0);
        w.EndArray();

        w.EndArray();
        return w.ToString();
    }

    private static List<int> ExtractAccountIds(Dictionary<string, List<string>> parameters)
    {
        List<int> ids = [];
        foreach (KeyValuePair<string, List<string>> kv in parameters)
        {
            if (!kv.Key.StartsWith("aids", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (string rawVal in kv.Value)
            {
                if (int.TryParse(rawVal.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int aid) && aid > 0)
                {
                    if (!ids.Contains(aid))
                    {
                        ids.Add(aid);
                    }
                }
            }
        }

        return ids;
    }

    private static string GetParam(Dictionary<string, List<string>> parameters, string key)
    {
        if (parameters.TryGetValue(key, out List<string>? values) && values.Count > 0)
        {
            return values[0];
        }

        return string.Empty;
    }

    private static async Task<ParsedHttpRequest?> ReadHttpRequestAsync(NetworkStream stream, CancellationToken token)
    {
        byte[] headerBuffer = new byte[MaxRequestHeaderBytes];
        int headerTotal = 0;
        int headerEndIndex = -1;

        while (headerTotal < headerBuffer.Length)
        {
            int read = await stream.ReadAsync(
                headerBuffer.AsMemory(headerTotal, headerBuffer.Length - headerTotal),
                token).ConfigureAwait(false);
            if (read <= 0)
            {
                return null;
            }

            headerTotal += read;
            headerEndIndex = FindDoubleCrlf(headerBuffer, headerTotal);
            if (headerEndIndex >= 0)
            {
                break;
            }
        }

        if (headerEndIndex < 0)
        {
            return null;
        }

        string headerText = Encoding.ASCII.GetString(headerBuffer, 0, headerEndIndex);
        string[] lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
        {
            return null;
        }

        string[] requestLineParts = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (requestLineParts.Length < 2)
        {
            return null;
        }

        string method = requestLineParts[0].Trim();
        string rawTarget = requestLineParts[1].Trim();

        int contentLength = 0;
        bool expectsContinue = false;
        for (int i = 1; i < lines.Length; i++)
        {
            int colon = lines[i].IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            string hName = lines[i][..colon].Trim();
            string hVal = lines[i][(colon + 1)..].Trim();
            if (hName.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(hVal, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedLen) && parsedLen > 0)
                {
                    contentLength = Math.Min(parsedLen, MaxRequestBodyBytes);
                }
            }
            else if (hName.Equals("Expect", StringComparison.OrdinalIgnoreCase) &&
                     hVal.Contains("100-continue", StringComparison.OrdinalIgnoreCase))
            {
                expectsContinue = true;
            }
        }

        int bodyStartOffset = headerEndIndex + 4;
        int alreadyBufferedBody = Math.Max(0, headerTotal - bodyStartOffset);

        if (expectsContinue && alreadyBufferedBody < contentLength)
        {
            byte[] continueBytes = "HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray();
            await stream.WriteAsync(continueBytes, token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
        }

        string bodyText = string.Empty;
        if (contentLength > 0)
        {
            byte[] bodyBytes = new byte[contentLength];
            int copied = Math.Min(alreadyBufferedBody, contentLength);
            if (copied > 0)
            {
                Buffer.BlockCopy(headerBuffer, bodyStartOffset, bodyBytes, 0, copied);
            }

            int bodyReadTotal = copied;
            while (bodyReadTotal < contentLength)
            {
                int r = await stream.ReadAsync(
                    bodyBytes.AsMemory(bodyReadTotal, contentLength - bodyReadTotal),
                    token).ConfigureAwait(false);
                if (r <= 0)
                {
                    break;
                }

                bodyReadTotal += r;
            }

            bodyText = Encoding.UTF8.GetString(bodyBytes, 0, bodyReadTotal);
        }

        string path = rawTarget;
        string query = string.Empty;
        int qIdx = rawTarget.IndexOf('?');
        if (qIdx >= 0)
        {
            path = rawTarget[..qIdx];
            query = rawTarget[(qIdx + 1)..];
        }

        Dictionary<string, List<string>> parameters = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        ParseFormUrlEncodedInto(query, parameters);
        ParseFormUrlEncodedInto(bodyText, parameters);

        return new ParsedHttpRequest(method, path, parameters);
    }

    private static void ParseFormUrlEncodedInto(string rawEncoded, Dictionary<string, List<string>> target)
    {
        if (string.IsNullOrWhiteSpace(rawEncoded))
        {
            return;
        }

        string[] pairs = rawEncoded.Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (string pair in pairs)
        {
            int eq = pair.IndexOf('=');
            string rawKey;
            string rawVal;
            if (eq >= 0)
            {
                rawKey = pair[..eq];
                rawVal = pair[(eq + 1)..];
            }
            else
            {
                rawKey = pair;
                rawVal = string.Empty;
            }

            string decodedKey = DecodeUrlComponent(rawKey);
            string decodedVal = DecodeUrlComponent(rawVal);
            if (string.IsNullOrEmpty(decodedKey))
            {
                continue;
            }

            if (!target.TryGetValue(decodedKey, out List<string>? list))
            {
                list = [];
                target[decodedKey] = list;
            }

            list.Add(decodedVal);
        }
    }

    private static string DecodeUrlComponent(string component)
    {
        if (string.IsNullOrEmpty(component))
        {
            return string.Empty;
        }

        string plusReplaced = component.Replace('+', ' ');
        try
        {
            return Uri.UnescapeDataString(plusReplaced);
        }
        catch (UriFormatException)
        {
            return plusReplaced;
        }
    }

    private static int FindDoubleCrlf(byte[] buffer, int length)
    {
        for (int i = 0; i <= length - 4; i++)
        {
            if (buffer[i] == (byte)'\r' &&
                buffer[i + 1] == (byte)'\n' &&
                buffer[i + 2] == (byte)'\r' &&
                buffer[i + 3] == (byte)'\n')
            {
                return i;
            }
        }

        return -1;
    }

    private sealed record ParsedHttpRequest(
        string Method,
        string Path,
        Dictionary<string, List<string>> Parameters);

    /// <summary>
    /// Reflection-free, NativeAOT-clean PHP <c>serialize()</c> wire writer with exact UTF-8 byte counts
    /// and strict entry-count assertions so serialized associative arrays can never drift out of sync.
    /// </summary>
    private sealed class PhpWireBuilder
    {
        private readonly StringBuilder _sb = new(1024);
        private readonly Stack<ArrayFrame> _frames = new();

        public void BeginArray(int expectedEntries)
        {
            if (_frames.Count > 0)
            {
                ArrayFrame parent = _frames.Peek();
                if (parent.ExpectingValue)
                {
                    parent.ExpectingValue = false;
                }
            }

            _sb.Append("a:");
            _sb.Append(expectedEntries.ToString(CultureInfo.InvariantCulture));
            _sb.Append(":{");
            _frames.Push(new ArrayFrame(expectedEntries));
        }

        public void EndArray()
        {
            ArrayFrame completed = _frames.Pop();
            if (completed.ActualEntries != completed.ExpectedEntries)
            {
                throw new InvalidOperationException(
                    $"PHP array entry count mismatch: declared {completed.ExpectedEntries}, wrote {completed.ActualEntries}.");
            }

            _sb.Append('}');
        }

        public void WriteKey(int index)
        {
            MarkKeyWritten();
            _sb.Append("i:");
            _sb.Append(index.ToString(CultureInfo.InvariantCulture));
            _sb.Append(';');
        }

        public void WriteKey(string key)
        {
            MarkKeyWritten();
            if ((int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out int numericKey) &&
                 !key.StartsWith('0')) ||
                key == "0")
            {
                int parsedInt = key == "0" ? 0 : numericKey;
                _sb.Append("i:");
                _sb.Append(parsedInt.ToString(CultureInfo.InvariantCulture));
                _sb.Append(';');
                return;
            }

            WriteRawPhpString(key);
        }

        public void WriteStringValue(string value)
        {
            MarkValueWritten();
            WriteRawPhpString(value);
        }

        public void WriteBoolValue(bool value)
        {
            MarkValueWritten();
            _sb.Append(value ? "b:1;" : "b:0;");
        }

        public void WriteStringEntry(string key, string value)
        {
            WriteKey(key);
            WriteStringValue(value);
        }

        public void WriteIndexedString(int index, string value)
        {
            WriteKey(index);
            WriteStringValue(value);
        }

        public void WriteIntEntry(string key, int value)
        {
            WriteKey(key);
            MarkValueWritten();
            _sb.Append("i:");
            _sb.Append(value.ToString(CultureInfo.InvariantCulture));
            _sb.Append(';');
        }

        public void WriteLongEntry(string key, long value)
        {
            WriteKey(key);
            MarkValueWritten();
            _sb.Append("i:");
            _sb.Append(value.ToString(CultureInfo.InvariantCulture));
            _sb.Append(';');
        }

        public void WriteDoubleEntry(string key, double value)
        {
            WriteKey(key);
            MarkValueWritten();
            _sb.Append("d:");
            _sb.Append(value.ToString(CultureInfo.InvariantCulture));
            _sb.Append(';');
        }

        public void WriteBoolEntry(string key, bool value)
        {
            WriteKey(key);
            WriteBoolValue(value);
        }

        public void WriteNullEntry(string key)
        {
            WriteKey(key);
            MarkValueWritten();
            _sb.Append("N;");
        }

        public override string ToString() => _sb.ToString();

        private void WriteRawPhpString(string value)
        {
            int byteCount = Encoding.UTF8.GetByteCount(value);
            _sb.Append("s:");
            _sb.Append(byteCount.ToString(CultureInfo.InvariantCulture));
            _sb.Append(":\"");
            _sb.Append(value);
            _sb.Append("\";");
        }

        private void MarkKeyWritten()
        {
            ArrayFrame top = _frames.Peek();
            if (top.ExpectingValue)
            {
                throw new InvalidOperationException("Cannot write two consecutive PHP array keys without a value.");
            }

            top.ActualEntries++;
            top.ExpectingValue = true;
        }

        private void MarkValueWritten()
        {
            if (_frames.Count > 0)
            {
                ArrayFrame top = _frames.Peek();
                top.ExpectingValue = false;
            }
        }

        private sealed class ArrayFrame(int expectedEntries)
        {
            public int ExpectedEntries { get; } = expectedEntries;
            public int ActualEntries { get; set; }
            public bool ExpectingValue { get; set; }
        }
    }
}
