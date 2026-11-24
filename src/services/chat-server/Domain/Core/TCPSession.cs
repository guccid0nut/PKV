// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ReSharper disable VirtualMemberNeverOverridden.Global
// ReSharper disable UnusedMethodReturnValue.Global
// ReSharper disable UnusedParameter.Global

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;

namespace TRANSMUTANSTEIN.ChatServer.Domain.Core;

/// <summary>
///     TCP Session Is Used To Read And Write Data From The Connected TCP Client
/// </summary>
/// <remarks>Thread-Safe</remarks>
/// <remarks>
///     Initialize The Session With A Given Server
/// </remarks>
/// <param name="server">TCP Server</param>
/// <param name="logger">Logger Instance (Optional)</param>
[SuppressMessage("ReSharper", "VirtualMemberNeverOverridden.Global")]
[SuppressMessage("ReSharper", "UnusedMethodReturnValue.Global")]
[SuppressMessage("Naming", "CA1716:Identifiers should not match keywords",
    Justification = "Virtual method parameter naming")]
public partial class TCPSession(TCPServer server, ILogger? logger = null) : IDisposable
{
    protected readonly CancellationTokenSource SessionCts = new();

    protected ILogger Logger { get; } = logger ?? NullLogger.Instance;

    /// <summary>
    ///     Cancellation token bound to this session lifetime.
    /// </summary>
    public CancellationToken CancellationToken => SessionCts.Token;

    /// <summary>
    ///     Session ID
    /// </summary>
    public Guid ID { get; } = Guid.CreateVersion7();

    /// <summary>
    ///     Server
    /// </summary>
    public TCPServer Server { get; } = server;

    /// <summary>
    ///     Socket
    /// </summary>
    public Socket Socket { get; private set; } = null!;

    /// <summary>
    ///     Number Of Bytes Pending Sent By The Session
    /// </summary>
    public long BytesPending { get; private set; }

    /// <summary>
    ///     Number Of Bytes Sending By The Session
    /// </summary>
    public long BytesSending { get; private set; }

    /// <summary>
    ///     Number Of Bytes Sent By The Session
    /// </summary>
    public long BytesSent { get; private set; }

    /// <summary>
    ///     Number Of Bytes Received By The Session
    /// </summary>
    public long BytesReceived { get; private set; }

    /// <summary>
    ///     Option: Receive Buffer Limit
    /// </summary>
    public int OptionReceiveBufferLimit { get; set; }

    /// <summary>
    ///     Option: Receive Buffer Size
    /// </summary>
    public int OptionReceiveBufferSize { get; set; } = server?.OptionReceiveBufferSize ?? 8192;

    /// <summary>
    ///     Option: Send Buffer Limit (defaults to 1 MiB per session when unset to bound slow-consumer memory growth)
    /// </summary>
    public int OptionSendBufferLimit { get; set; } = server is { OptionSendBufferLimit: > 0 } ? server.OptionSendBufferLimit : 1024 * 1024;

    /// <summary>
    ///     Option: Send Buffer Size
    /// </summary>
    public int OptionSendBufferSize { get; set; } = server?.OptionSendBufferSize ?? 8192;

    /// <summary>
    ///     When true, delegates socket receive handling to a custom pipeline (e.g. PipeSocketHandler in ChatSession)
    ///     instead of launching the legacy SocketAsyncEventArgs receive loop.
    /// </summary>
    protected virtual bool EnableCustomSocketIngress => false;

    [LoggerMessage(Level = LogLevel.Information, Message = "Chat Session ID {SessionID} Was Created")]
    private partial void LogSessionCreated(Guid sessionId);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Chat Session ID {SessionID} Caught A Socket Error With Code {SocketErrorCode}")]
    private partial void LogSocketError(Guid sessionId, SocketError socketErrorCode);

    [LoggerMessage(Level = LogLevel.Information, Message = "Chat Session ID {SessionID} Has Terminated")]
    private partial void LogSessionTerminated(Guid sessionId);

    [LoggerMessage(Level = LogLevel.Error,
        Message =
            "[CRITICAL] Error In TCPSession.OnAsyncCompleted. IsSocketDisposed: {IsSocketDisposed}, IsConnected: {IsConnected}, e is null: {EIsNull}")]
    private partial void LogAsyncCompletedError(Exception ex, bool isSocketDisposed, bool isConnected, bool eIsNull);

    # region Connect/Disconnect Session

    /// <summary>
    ///     Is The Session Connected?
    /// </summary>
    public bool IsConnected { get; private set; }

    /// <summary>
    ///     Connect The Session
    /// </summary>
    /// <param name="socket">Session Socket</param>
    internal void Connect(Socket socket)
    {
        Socket = socket;

        // Update The Session Socket Disposed Flag
        IsSocketDisposed = false;

        // Set Up Buffers
        _receiveBuffer = new TCPBuffer();
        _sendBufferMain = new TCPBuffer();
        _sendBufferFlush = new TCPBuffer();

        // Set Up Event Args
        _receiveEventArg = new SocketAsyncEventArgs();
        _receiveEventArg.Completed += OnAsyncCompleted;
        _sendEventArg = new SocketAsyncEventArgs();
        _sendEventArg.Completed += OnAsyncCompleted;

        // Apply The Option: Keep Alive
        if (Server.OptionKeepAlive) Socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

        if (Server.OptionTCPKeepAliveTime >= 0)
            Socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime,
                Server.OptionTCPKeepAliveTime);

        if (Server.OptionTCPKeepAliveInterval >= 0)
            Socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval,
                Server.OptionTCPKeepAliveInterval);

        if (Server.OptionTCPKeepAliveRetryCount >= 0)
            Socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount,
                Server.OptionTCPKeepAliveRetryCount);

        // Apply The Option: No Delay
        if (Server.OptionNoDelay) Socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.NoDelay, true);

        // Prepare Receive/Send Buffers
        _receiveBuffer.Reserve(OptionReceiveBufferSize);
        _sendBufferMain.Reserve(OptionSendBufferSize);
        _sendBufferFlush.Reserve(OptionSendBufferSize);

        // Reset Statistic
        BytesPending = 0;
        BytesSending = 0;
        BytesSent = 0;
        BytesReceived = 0;

        // Call The Session Connecting Handler
        OnConnecting();

        // Call The Session Connecting Handler In The Server
        Server.OnConnectingInternal(this);

        // Update The Connected Flag
        IsConnected = true;

        // Try To Receive Something From The Client (Unless Delegated To Custom Ingress Pipeline)
        if (!EnableCustomSocketIngress)
        {
            TryReceive();
        }

        // Check The Socket Disposed State: In Some Rare Cases It Might Be Disconnected While Receiving!
        if (IsSocketDisposed) return;

        // Call The Session Connected Handler
        OnConnected();

        // Call The Session Connected Handler In The Server
        Server.OnConnectedInternal(this);

        // Call The Empty Send Buffer Handler
        if (_sendBufferMain.IsEmpty) OnEmpty();
    }

    /// <summary>
    ///     Disconnect The Session
    /// </summary>
    /// <returns>TRUE If The Section Was Successfully Disconnected, Or FALSE If The Section Is Already Disconnected</returns>
    public virtual bool Disconnect()
    {
        if (!IsConnected) return false;

        try
        {
            SessionCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        // Reset Event Args
        _receiveEventArg.Completed -= OnAsyncCompleted;
        _sendEventArg.Completed -= OnAsyncCompleted;

        // Call The Session Disconnecting Handler
        OnDisconnecting();

        // Call The Session Disconnecting Handler In The Server
        Server.OnDisconnectingInternal(this);

        try
        {
            try
            {
                // Shutdown The Socket Associated With The Client
                Socket.Shutdown(SocketShutdown.Both);
            }

            catch (SocketException)
            {
            }

            // Close The Session Socket
            Socket.Close();

            // Dispose The Session Socket
            Socket.Dispose();

            // Dispose Event Arguments
            _receiveEventArg.Dispose();
            _sendEventArg.Dispose();

            // Update The Session Socket Disposed Flag
            IsSocketDisposed = true;
        }

        catch (ObjectDisposedException)
        {
        }

        // Update The Connected Flag
        IsConnected = false;

        // Update Sending/Receiving Flags
        _receiving = false;
        _sending = false;

        // Clear Send/Receive Buffers
        ClearBuffers();

        // Call The Session Disconnected Handler
        OnDisconnected();

        // Call The Session Disconnected Handler In The Server
        Server.OnDisconnectedInternal(this);

        // Unregister Session
        Server.UnregisterSession(ID);

        return true;
    }

    # endregion

    # region Send/Receive Data

    // Receive Buffer
    private bool _receiving;
    private TCPBuffer _receiveBuffer = null!;
    private SocketAsyncEventArgs _receiveEventArg = null!;

    // Send Buffer
    private readonly Lock _sendLock = new();
    private bool _sending;
    private TCPBuffer _sendBufferMain = null!;
    private TCPBuffer _sendBufferFlush = null!;
    private SocketAsyncEventArgs _sendEventArg = null!;
    private long _sendBufferFlushOffset;

    /// <summary>
    ///     Send Data To The Client (Synchronous)
    /// </summary>
    /// <param name="buffer">Buffer To Send</param>
    /// <returns>Size Of Sent Data</returns>
    public virtual long Send(byte[] buffer)
    {
        return Send(buffer.AsSpan());
    }

    /// <summary>
    ///     Send Data To The Client (Synchronous)
    /// </summary>
    /// <param name="buffer">Buffer To Send</param>
    /// <param name="offset">Buffer Offset</param>
    /// <param name="size">Buffer Size</param>
    /// <returns>Size Of Sent Data</returns>
    public virtual long Send(byte[] buffer, long offset, long size)
    {
        return Send(buffer.AsSpan((int)offset, (int)size));
    }

    /// <summary>
    ///     Send Data To The Client (Synchronous)
    /// </summary>
    /// <param name="buffer">Buffer To Send As A Span Of Bytes</param>
    /// <returns>Size Of Sent Data</returns>
    public virtual long Send(ReadOnlySpan<byte> buffer)
    {
        if (!IsConnected) return 0;

        if (buffer.IsEmpty) return 0;

        // Sent Data To The Client
        long sent = Socket.Send(buffer, SocketFlags.None, out SocketError ec);

        if (sent > 0)
        {
            // Update Statistic
            BytesSent += sent;
            Interlocked.Add(ref Server._bytesSent, sent);

            // Call The Buffer Sent Handler
            OnSent(sent, BytesPending + BytesSending);
        }

        // Check For Socket Error
        if (ec != SocketError.Success)
        {
            SendError(ec);
            Disconnect();
        }

        return sent;
    }

    /// <summary>
    ///     Send Text To The Client (Synchronous)
    /// </summary>
    /// <param name="text">Text String To Send</param>
    /// <returns>Size Of Sent Data</returns>
    public virtual long Send(string text)
    {
        return Send(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>
    ///     Send Text To The Client (Synchronous)
    /// </summary>
    /// <param name="text">Text To Send As A Span Of Characters</param>
    /// <returns>Size Of Sent Data</returns>
    public virtual long Send(ReadOnlySpan<char> text)
    {
        int maxBytes = Encoding.UTF8.GetMaxByteCount(text.Length);
        if (maxBytes <= 512)
        {
            Span<byte> buffer = stackalloc byte[maxBytes];
            int bytesWritten = Encoding.UTF8.GetBytes(text, buffer);
            return Send(buffer[..bytesWritten]);
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(maxBytes);
        try
        {
            int bytesWritten = Encoding.UTF8.GetBytes(text, rented);
            return Send(rented.AsSpan(0, bytesWritten));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    ///     Send Data To The Client (Asynchronous)
    /// </summary>
    /// <param name="buffer">Buffer To Send</param>
    /// <returns>TRUE If The Data Was Successfully Sent, Or FALSE If The Session Is Not Connected</returns>
    public virtual bool SendAsync(byte[] buffer)
    {
        return SendAsync(buffer.AsSpan());
    }

    /// <summary>
    ///     Send Data To The Client (Asynchronous)
    /// </summary>
    /// <param name="buffer">Buffer To Send</param>
    /// <param name="offset">Buffer Offset</param>
    /// <param name="size">Buffer Size</param>
    /// <returns>TRUE If The Data Was Successfully Sent, Or FALSE If The Session Is Not Connected</returns>
    public virtual bool SendAsync(byte[] buffer, long offset, long size)
    {
        return SendAsync(buffer.AsSpan((int)offset, (int)size));
    }

    /// <summary>
    ///     Send Data To The Client (Asynchronous)
    /// </summary>
    /// <param name="buffer">Buffer To Send As A Span Of Bytes</param>
    /// <returns>TRUE If The Data Was Successfully Sent, Or FALSE If The Session Is Not Connected</returns>
    public virtual bool SendAsync(ReadOnlySpan<byte> buffer)
    {
        if (!IsConnected) return false;

        if (buffer.IsEmpty) return true;

        lock (_sendLock)
        {
            // Check The Send Buffer Limit
            if (_sendBufferMain.Size + buffer.Length > OptionSendBufferLimit && OptionSendBufferLimit > 0)
            {
                SendError(SocketError.NoBufferSpaceAvailable);

                return false;
            }

            // Fill The Main Send Buffer
            _sendBufferMain.Append(buffer);

            // Update Statistic
            BytesPending = _sendBufferMain.Size;

            // Avoid Multiple Send Handlers
            if (_sending) return true;

            _sending = true;

            // Try To Send The Main Buffer
            TrySend();
        }

        return true;
    }

    /// <summary>
    ///     Send Text To The Client (Asynchronous)
    /// </summary>
    /// <param name="text">Text String To Send</param>
    /// <returns>TRUE If The Text Was Successfully Sent, Or FALSE If The Session Is Not Connected</returns>
    public virtual bool SendAsync(string text)
    {
        return SendAsync(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>
    ///     Send Text To The Client (Asynchronous)
    /// </summary>
    /// <param name="text">Text To Send As A Span Of Characters</param>
    /// <returns>TRUE If The Text Was Successfully Sent, Or FALSE If The Session Is Not Connected</returns>
    public virtual bool SendAsync(ReadOnlySpan<char> text)
    {
        int maxBytes = Encoding.UTF8.GetMaxByteCount(text.Length);
        if (maxBytes <= 512)
        {
            Span<byte> buffer = stackalloc byte[maxBytes];
            int bytesWritten = Encoding.UTF8.GetBytes(text, buffer);
            return SendAsync(buffer[..bytesWritten]);
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(maxBytes);
        try
        {
            int bytesWritten = Encoding.UTF8.GetBytes(text, rented);
            return SendAsync(rented.AsSpan(0, bytesWritten));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    ///     Receive Data From The Client (Synchronous)
    /// </summary>
    /// <param name="buffer">Buffer To Receive</param>
    /// <returns>Size Of Received Data</returns>
    public virtual long Receive(byte[] buffer)
    {
        return Receive(buffer, 0, buffer.Length);
    }

    /// <summary>
    ///     Receive Data From The Client (Synchronous)
    /// </summary>
    /// <param name="buffer">Buffer To Receive</param>
    /// <param name="offset">Buffer Offset</param>
    /// <param name="size">Buffer Size</param>
    /// <returns>Size Of Received Data</returns>
    public virtual long Receive(byte[] buffer, long offset, long size)
    {
        if (!IsConnected) return 0;

        if (size == 0) return 0;

        // Receive Data From The Client
        long received = Socket.Receive(buffer, (int)offset, (int)size, SocketFlags.None, out SocketError ec);

        if (received > 0)
        {
            // Update Statistic
            BytesReceived += received;
            Interlocked.Add(ref Server._bytesReceived, received);

            // Call The Buffer Received Handler
            OnReceived(buffer, 0, received);
        }

        // Check For Socket Error
        if (ec != SocketError.Success)
        {
            SendError(ec);
            Disconnect();
        }

        return received;
    }

    /// <summary>
    ///     Receive Text From The Client (Synchronous)
    /// </summary>
    /// <param name="size">Text Size To Receive</param>
    /// <returns>Received Text</returns>
    public virtual string Receive(long size)
    {
        byte[] buffer = new byte[size];
        long length = Receive(buffer);

        return Encoding.UTF8.GetString(buffer, 0, (int)length);
    }

    /// <summary>
    ///     Receive Data From The Client (Asynchronous)
    /// </summary>
    public virtual void ReceiveAsync()
    {
        // Try To Receive Data From The Client
        TryReceive();
    }

    /// <summary>
    ///     Try To Receive New Data
    /// </summary>
    private void TryReceive()
    {
        if (_receiving) return;

        if (!IsConnected) return;

        bool process = true;

        while (process)
        {
            process = false;

            try
            {
                // Async Receive With The Reception Handler
                _receiving = true;
                _receiveEventArg.SetBuffer(_receiveBuffer.Data, 0, (int)_receiveBuffer.Capacity);

                if (!Socket.ReceiveAsync(_receiveEventArg)) process = ProcessReceive(_receiveEventArg);
            }

            catch (ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>
    ///     Try To Send Pending Data
    /// </summary>
    private void TrySend()
    {
        if (!IsConnected) return;

        bool empty = false;
        bool process = true;

        while (process)
        {
            process = false;

            lock (_sendLock)
            {
                // Is Previous Socket Send In Progress?
                if (_sendBufferFlush.IsEmpty)
                {
                    // Swap Flush And Main Buffers
                    _sendBufferFlush = Interlocked.Exchange(ref _sendBufferMain, _sendBufferFlush);
                    _sendBufferFlushOffset = 0;

                    // Update Statistic
                    BytesPending = 0;
                    BytesSending += _sendBufferFlush.Size;

                    // Check If The Flush Buffer Is Empty
                    if (_sendBufferFlush.IsEmpty)
                    {
                        // Need To Call Empty Send Buffer Handler
                        empty = true;

                        // End Sending Process
                        _sending = false;
                    }
                }

                else
                {
                    return;
                }
            }

            // Call The Empty Send Buffer Handler
            if (empty)
            {
                OnEmpty();
                return;
            }

            try
            {
                // Async Write With The Write Handler
                _sendEventArg.SetBuffer(_sendBufferFlush.Data, (int)_sendBufferFlushOffset,
                    (int)(_sendBufferFlush.Size - _sendBufferFlushOffset));

                if (!Socket.SendAsync(_sendEventArg)) process = ProcessSend(_sendEventArg);
            }

            catch (ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>
    ///     Clear Send/Receive Buffers
    /// </summary>
    private void ClearBuffers()
    {
        lock (_sendLock)
        {
            // Clear Send Buffers
            _sendBufferMain.Clear();
            _sendBufferFlush.Clear();
            _sendBufferFlushOffset = 0;

            // Update Statistic
            BytesPending = 0;
            BytesSending = 0;
        }
    }

    #endregion

    #region IO Processing

    /// <summary>
    ///     This Method Is Called Whenever A Reception Or Send Operation Is Completed On A Socket
    /// </summary>
    private void OnAsyncCompleted(object? sender, SocketAsyncEventArgs e)
#nullable disable
    {
        if (IsSocketDisposed) return;

        try
        {
            // Determine Which Type Of Operation Just Completed And Call The Associated Handler
            switch (e.LastOperation)
            {
                case SocketAsyncOperation.Receive:
                    if (ProcessReceive(e)) TryReceive();

                    break;
                case SocketAsyncOperation.Send:
                    if (ProcessSend(e)) TrySend();

                    break;
                case SocketAsyncOperation.None:
                case SocketAsyncOperation.Accept:
                case SocketAsyncOperation.Connect:
                case SocketAsyncOperation.Disconnect:
                case SocketAsyncOperation.ReceiveFrom:
                case SocketAsyncOperation.ReceiveMessageFrom:
                case SocketAsyncOperation.SendPackets:
                case SocketAsyncOperation.SendTo:
                default:
                    throw new ArgumentException("The Last Operation Completed On The Socket Was Not A Receive Or Send");
            }
        }
        catch (Exception ex)
        {
            try
            {
                LogAsyncCompletedError(ex, IsSocketDisposed, IsConnected, e is null);
                Disconnect();
            }
            catch
            {
                /* Ignore logging failures during crash */
            }
        }
    }

    /// <summary>
    ///     This Method Is Invoked When An Asynchronous Reception Operation Completes
    /// </summary>
    private bool ProcessReceive(SocketAsyncEventArgs e)
    {
        if (e is null) return false;

        if (!IsConnected) return false;

        long size = e.BytesTransferred;

        // Received Some Data From The Client
        if (size > 0)
        {
            // Update Statistic
            BytesReceived += size;
            Interlocked.Add(ref Server._bytesReceived, size);

            // Call The Buffer Received Handler
            OnReceived(_receiveBuffer.Data, 0, size);

            // If The Reception Buffer Is Full Increase Its Size
            if (_receiveBuffer.Capacity == size)
            {
                // Check The Receive Buffer Limit
                if (2 * size > OptionReceiveBufferLimit && OptionReceiveBufferLimit > 0)
                {
                    SendError(SocketError.NoBufferSpaceAvailable);
                    Disconnect();

                    return false;
                }

                _receiveBuffer.Reserve(2 * size);
            }
        }

        _receiving = false;

        // Try To Receive Again If The Session Is Valid
        if (e.SocketError == SocketError.Success)
        {
            // If Zero Is Returned From A Read Operation, The Remote End Has Closed The Connection
            if (size > 0) return true;
        }
        else
        {
            SendError(e.SocketError);
        }

        Disconnect();

        return false;
    }

    /// <summary>
    ///     This Method Is Invoked When An Asynchronous Send Operation Completes
    /// </summary>
    private bool ProcessSend(SocketAsyncEventArgs e)
    {
        if (!IsConnected) return false;

        long size = e.BytesTransferred;

        // Send Some Data To The Client
        if (size > 0)
        {
            // Update Statistic
            BytesSending -= size;
            BytesSent += size;
            Interlocked.Add(ref Server._bytesSent, size);

            // Increase The Flush Buffer Offset
            _sendBufferFlushOffset += size;

            // Successfully Send The Whole Flush Buffer
            if (_sendBufferFlushOffset == _sendBufferFlush.Size)
            {
                // Clear The Flush Buffer
                _sendBufferFlush.Clear();
                _sendBufferFlushOffset = 0;
            }

            // Call The Buffer Sent Handler
            OnSent(size, BytesPending + BytesSending);
        }

        // Try To Send Again If The Session Is Valid
        if (e.SocketError == SocketError.Success) return true;

        SendError(e.SocketError);
        Disconnect();

        return false;
    }

    # endregion

    # region Session handlers

    /// <summary>
    ///     Handle Client Connecting Notification
    /// </summary>
    protected virtual void OnConnecting()
    {
    }

    /// <summary>
    ///     Handle Client Connected Notification
    /// </summary>
    protected virtual void OnConnected()
    {
        LogSessionCreated(ID);
    }

    /// <summary>
    ///     Handle Client Disconnecting Notification
    /// </summary>
    protected virtual void OnDisconnecting()
    {
    }

    /// <summary>
    ///     Handle Client Disconnected Notification
    /// </summary>
    protected virtual void OnDisconnected()
    {
        LogSessionTerminated(ID);
    }

    /// <summary>
    ///     Handle Buffer Received Notification
    /// </summary>
    /// <param name="buffer">Received Buffer</param>
    /// <param name="offset">Received Buffer Offset</param>
    /// <param name="size">Received Buffer Size</param>
    /// <remarks>
    ///     Notification Is Called When Another Part Of Buffer Was Received From The Client
    /// </remarks>
    protected virtual void OnReceived(byte[] buffer, long offset, long size)
    {
    }

    /// <summary>
    ///     Handle Buffer Sent Notification
    /// </summary>
    /// <param name="sent">Size Of Sent Buffer</param>
    /// <param name="pending">Size Of Pending Buffer</param>
    /// <remarks>
    ///     Notification Is Called When Another Part Of Buffer Was Sent To The Client
    ///     <br />
    ///     This Handler Could Be Used To Send Another Buffer To The Client For Instance When The Pending Size Is Zero
    /// </remarks>
    protected virtual void OnSent(long sent, long pending)
    {
    }

    /// <summary>
    ///     Handle Empty Send Buffer Notification
    /// </summary>
    /// <remarks>
    ///     Notification Is Called When The Send Buffer Is Empty And Ready For A New Data To Send
    ///     <br />
    ///     This Handler Could Be Used To Send Another Buffer To The Client
    /// </remarks>
    protected virtual void OnEmpty()
    {
    }

    /// <summary>
    ///     Handle Error Notification
    /// </summary>
    /// <param name="socketError">Socket Error Code</param>
    protected virtual void OnError(SocketError socketError)
    {
        LogSocketError(ID, socketError);
    }

    # endregion

    # region Error handling

    /// <summary>
    ///     Send Error Notification
    /// </summary>
    /// <param name="socketError">Socket Error Code</param>
    private void SendError(SocketError socketError)
    {
        // Skip Disconnect Errors
        if (socketError is SocketError.ConnectionAborted or SocketError.ConnectionRefused or SocketError.ConnectionReset
            or SocketError.OperationAborted or SocketError.Shutdown)
            return;

        OnError(socketError);
    }

    # endregion

    # region IDisposable Implementation

    /// <summary>
    ///     Disposed Flag
    /// </summary>
    public bool IsDisposed { get; private set; }

    /// <summary>
    ///     Session Socket Disposed Flag
    /// </summary>
    public bool IsSocketDisposed { get; private set; } = true;

    // Implement IDisposable
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposingManagedResources)
    {
        // The idea here is that Dispose(Boolean) knows whether it is being
        // called to do explicit cleanup (the Boolean is true) versus being
        // called due to a garbage collection event (the Boolean is false)
        // This distinction is useful because, when being disposed explicitly
        // the Dispose(Boolean) method can safely execute code using reference
        // type fields that refer to other objects knowing for sure
        // that these other objects have not been finalized or disposed of yet
        // When the Boolean is false, the Dispose(Boolean) method should not
        // execute code that refers to reference type fields,
        // because those objects may have already been finalized

        if (!IsDisposed)
        {
            if (disposingManagedResources)
            {
                // Dispose Managed Resources Here ...
                Disconnect();
                try
                {
                    SessionCts.Dispose();
                }
                catch (ObjectDisposedException)
                {
                }
            }

            // Dispose Unmanaged Resources Here ...

            // Set Large Fields To NULL Here ...

            // Mark As Disposed
            IsDisposed = true;
        }
    }

    # endregion
}
