// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: VoiceClientService.cs
// System: ProjectKongor - Opus Voice Subsystem
// Policy: Strict Explicit Typing (NO 'var') - High-Performance Client Voice Engine
// ============================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using ASPIRE.Common.Netcode.Sockets;
using Kongor.Core.Netcode.Voice;

namespace Kongor.Core.Services.Voice;

/// <summary>
///     Represents the decoded audio frame ready for playback and UI state notifications.
/// </summary>
public readonly struct ReceivedVoiceFrame
{
    public uint SenderAccountId { get; }
    public VoiceChannelType ChannelType { get; }
    public uint ChannelId { get; }
    public ushort Sequence { get; }
    public ReadOnlyMemory<short> PcmSamples { get; }

    public ReceivedVoiceFrame(
        uint senderAccountId,
        VoiceChannelType channelType,
        uint channelId,
        ushort sequence,
        ReadOnlyMemory<short> pcmSamples)
    {
        SenderAccountId = senderAccountId;
        ChannelType = channelType;
        ChannelId = channelId;
        Sequence = sequence;
        PcmSamples = pcmSamples;
    }
}

public interface IVoiceClientService : IAsyncDisposable, IDisposable
{
    uint LocalAccountId { get; set; }
    IPEndPoint? RelayEndPoint { get; set; }
    bool IsPushToTalkActive { get; }
    VoiceChannelType ActiveChannel { get; }
    uint ActiveChannelId { get; }
    float InputVolumePercent { get; set; }
    float GainDb { get; set; }
    bool EnableAiNoiseSuppression { get; set; }
    bool IsMuted { get; set; }
    DateTimeOffset? MuteExpiration { get; set; }
    string? MuteReason { get; set; }

    IWasapiAudioEngine? AudioEngine { get; set; }
    event Action<ReceivedVoiceFrame>? OnVoiceFrameReceived;
    event Action<uint, bool>? OnPeerSpeakingStateChanged;
    event Action<uint, bool, bool>? OnPeerVoiceStateUpdated;
    event Action<string>? OnMuteBlocked;

    void SetActiveChannel(VoiceChannelType channelType, uint channelId);
    void SetPushToTalkState(bool isPressed, VoiceChannelType channelType, uint channelId);
    void SubmitMicrophonePcm(ReadOnlySpan<short> samples);
    bool TransmitAudioFrame(ReadOnlySpan<short> pcmSamples, VoicePacketFlags flags = VoicePacketFlags.None);
    void SetPeerIgnored(uint peerAccountId, bool ignored);
    void SetIgnoredPeers(IEnumerable<uint> peerAccountIds);
    bool IsPeerIgnored(uint peerAccountId);
    void Start(int localPort = 0);
    Task StopAsync();
}

/// <summary>
///     Client-side voice engine coordinating Opus encoding, UDP datagram transmission,
///     peer stream decoding, packet loss concealment, and speaking activity detection.
/// </summary>
public sealed class VoiceClientService : IVoiceClientService, IDisposable, IAsyncDisposable
{
    private readonly IOpusVoiceCodec _encoderCodec;
    private readonly AiNoiseSuppressionProcessor _noiseSuppressor = new();
    private readonly ConcurrentDictionary<uint, IOpusVoiceCodec> _peerDecoders = new();
    private readonly ConcurrentDictionary<uint, long> _peerLastSeenTicks = new();
    private readonly ConcurrentDictionary<uint, bool> _peerSpeakingStates = new();
    private readonly ConcurrentDictionary<uint, ushort> _peerLastSequence = new();
    private readonly ConcurrentDictionary<uint, byte> _ignoredPeers = new();
    private readonly TimeProvider _timeProvider;
    private readonly Lock _transmitLock = new();

    private Socket? _udpSocket;
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveTask;
    private Task? _silenceSweepTask;
    private ushort _sequenceNumber;
    private bool _isDisposed;

    public uint LocalAccountId { get; set; }
    public IPEndPoint? RelayEndPoint { get; set; }
    public bool IsPushToTalkActive { get; private set; }
    public VoiceChannelType ActiveChannel { get; private set; } = VoiceChannelType.Party;
    public uint ActiveChannelId { get; private set; }
    public float InputVolumePercent { get; set; } = 100f;
    public float GainDb { get; set; } = 0f;
    public bool EnableAiNoiseSuppression { get; set; } = true;
    public NoiseSuppressionBackend ActiveNoiseSuppressionBackend => _noiseSuppressor.ActiveBackend;
    public bool IsNativeRnNoiseActive => _noiseSuppressor.IsNativeRnNoiseActive;
    public string NoiseSuppressionDiagnostic => _noiseSuppressor.BackendDiagnostic;
    public bool IsMuted { get; set; }
    public DateTimeOffset? MuteExpiration { get; set; }
    public string? MuteReason { get; set; }
    public IWasapiAudioEngine? AudioEngine { get; set; }
    public ScrcpyLockFreeSpscAudioBuffer MicrophoneSpscBuffer { get; } = new(16384);
    public ScrcpyAudioRegulator PlaybackRegulator { get; } = new();
    public event Action<ReceivedVoiceFrame>? OnVoiceFrameReceived;
    public event Action<uint, bool>? OnPeerSpeakingStateChanged;
    public event Action<uint, bool, bool>? OnPeerVoiceStateUpdated;
    public event Action<string>? OnMuteBlocked;

    public static uint GetTeamChannelId(uint matchId, int teamIndex)
    {
        return (matchId << 1) | (uint)Math.Clamp(teamIndex - 1, 0, 1);
    }

    public static uint GetLaneChannelId(uint matchId, int teamIndex, int laneIndex)
    {
        return (((matchId << 1) | (uint)Math.Clamp(teamIndex - 1, 0, 1)) * 3) + (uint)Math.Clamp(laneIndex, 0, 2);
    }

    public static uint GetMentorChannelId(uint pupilAccountId)
    {
        return pupilAccountId;
    }

    public void SubmitMicrophonePcm(ReadOnlySpan<short> samples)
    {
        if (IsPushToTalkActive && !samples.IsEmpty)
        {
            MicrophoneSpscBuffer.Write(samples);
            Span<short> drained = stackalloc short[samples.Length];
            int read = MicrophoneSpscBuffer.Read(drained);
            if (read > 0)
            {
                TransmitAudioFrame(drained.Slice(0, read));
            }
        }
    }

    private void OnAudioEngineFrameCaptured(ReadOnlyMemory<short> pcmFrame)
    {
        SubmitMicrophonePcm(pcmFrame.Span);
    }

    public VoiceClientService(
        uint localAccountId,
        IPEndPoint? relayEndPoint = null,
        TimeProvider? timeProvider = null)
    {
        LocalAccountId = localAccountId;
        RelayEndPoint = relayEndPoint;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _encoderCodec = new OpusVoiceCodec();
    }

    public void SetActiveChannel(VoiceChannelType channelType, uint channelId)
    {
        ActiveChannel = channelType;
        ActiveChannelId = channelId;
    }

    public void SetPushToTalkState(bool isPressed, VoiceChannelType channelType, uint channelId)
    {
        // Auto-expire active mute if time has lapsed
        if (IsMuted && MuteExpiration.HasValue && MuteExpiration.Value <= _timeProvider.GetUtcNow())
        {
            IsMuted = false;
            MuteExpiration = null;
            MuteReason = null;
        }

        if (isPressed && IsMuted)
        {
            IsPushToTalkActive = false;
            OnMuteBlocked?.Invoke("^279* You are currently muted and are unable to communicate.");
            return;
        }

        bool wasActive = IsPushToTalkActive;
        IsPushToTalkActive = isPressed;
        ActiveChannel = channelType;
        ActiveChannelId = channelId;

        if (wasActive && !isPressed)
        {
            // Send end-of-transmission frame with zero payload to immediately release remote speaker indicators
            TransmitAudioFrame(ReadOnlySpan<short>.Empty, VoicePacketFlags.EndOfTransmission);
        }
    }

    public void Start(int localPort = 0)
    {
        if (_udpSocket != null) return;

        _udpSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _udpSocket.ConfigureForFastRebind();
        _udpSocket.Bind(new IPEndPoint(IPAddress.Any, localPort));

        System.Diagnostics.Trace.WriteLine(
            $"[VOICE-RNNOISE] VoiceClientService started (AccountId={LocalAccountId}, ActiveBackend={ActiveNoiseSuppressionBackend}, EnableAiNoiseSuppression={EnableAiNoiseSuppression}) | {NoiseSuppressionDiagnostic}");

        _receiveCts = new CancellationTokenSource();
        _receiveTask = Task.Run(() => ReceiveLoopAsync(_receiveCts.Token), CancellationToken.None);
        _silenceSweepTask = Task.Run(() => SilenceSweepLoopAsync(_receiveCts.Token), CancellationToken.None);

        if (AudioEngine != null)
        {
            AudioEngine.OnPcmFrameCaptured += OnAudioEngineFrameCaptured;
            AudioEngine.Start();
        }
    }

    public bool TransmitAudioFrame(ReadOnlySpan<short> pcmSamples, VoicePacketFlags flags = VoicePacketFlags.None)
    {
        if (IsMuted)
        {
            if (MuteExpiration.HasValue && MuteExpiration.Value <= _timeProvider.GetUtcNow())
            {
                IsMuted = false;
                MuteExpiration = null;
                MuteReason = null;
            }
            else
            {
                return false;
            }
        }

        if (_udpSocket == null || RelayEndPoint == null || LocalAccountId == 0)
        {
            return false;
        }

        lock (_transmitLock)
        {
            byte[] payloadBuffer = Array.Empty<byte>();
            if (!pcmSamples.IsEmpty)
            {
                Span<short> processed = stackalloc short[pcmSamples.Length];
                pcmSamples.CopyTo(processed);

                _noiseSuppressor.ProcessFrame(
                    processed,
                    sampleRate: 48000,
                    volumePercent: InputVolumePercent,
                    gainDb: GainDb,
                    enableNoiseSuppression: EnableAiNoiseSuppression);

                byte[] encodedBytes = new byte[1276];
                int encodedLength = _encoderCodec.Encode(processed, encodedBytes);
                payloadBuffer = new byte[encodedLength];
                encodedBytes.AsSpan(0, encodedLength).CopyTo(payloadBuffer);
            }

            ushort seq = unchecked(++_sequenceNumber);
            VoiceDatagramPacket packet = new VoiceDatagramPacket(
                ActiveChannel,
                ActiveChannelId,
                LocalAccountId,
                seq,
                payloadBuffer,
                flags);

            byte[] packetBytes = packet.ToByteArray();
            try
            {
                _udpSocket.SendTo(packetBytes, RelayEndPoint);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[2048];
        EndPoint remote = new IPEndPoint(IPAddress.Any, 0);

        while (!cancellationToken.IsCancellationRequested)
        {
            Socket? socket = _udpSocket;
            if (socket == null)
            {
                break;
            }

            try
            {
                SocketReceiveFromResult result = await socket.ReceiveFromAsync(
                    buffer.AsMemory(),
                    SocketFlags.None,
                    remote,
                    cancellationToken).ConfigureAwait(false);

                ReadOnlyMemory<byte> rawBytes = buffer.AsMemory(0, result.ReceivedBytes);
                if (!VoiceDatagramPacket.TryParse(rawBytes, out VoiceDatagramPacket packet))
                {
                    continue;
                }

                if (packet.SenderAccountId == LocalAccountId)
                {
                    continue; // Skip echo of own voice
                }

                ProcessIncomingPacket(packet);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                break;
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested || _udpSocket == null)
            {
                break;
            }
            catch (Exception ex)
            {
                if (cancellationToken.IsCancellationRequested || _udpSocket == null)
                {
                    break;
                }

                System.Diagnostics.Debug.WriteLine($"[VoiceClientService] Transient receive error: {ex.Message}");
            }
        }
    }

    public void SetPeerIgnored(uint peerAccountId, bool ignored)
    {
        if (peerAccountId == 0) return;
        if (ignored)
        {
            _ignoredPeers[peerAccountId] = 1;
        }
        else
        {
            _ignoredPeers.TryRemove(peerAccountId, out _);
        }
    }

    public void SetIgnoredPeers(IEnumerable<uint> peerAccountIds)
    {
        _ignoredPeers.Clear();
        foreach (uint id in peerAccountIds)
        {
            if (id != 0)
            {
                _ignoredPeers[id] = 1;
            }
        }
    }

    public bool IsPeerIgnored(uint peerAccountId)
    {
        return peerAccountId != 0 && _ignoredPeers.ContainsKey(peerAccountId);
    }

    public void ProcessIncomingPacket(VoiceDatagramPacket packet)
    {
        uint senderId = packet.SenderAccountId;
        if (IsPeerIgnored(senderId))
        {
            return; // Zero Opus decode, zero speaking glow, zero state mutation
        }

        long nowTicks = Environment.TickCount64;
        _peerLastSeenTicks[senderId] = nowTicks;

        if (packet.Flags.HasFlag(VoicePacketFlags.EndOfTransmission))
        {
            if (_peerSpeakingStates.TryGetValue(senderId, out bool isSpeaking) && isSpeaking)
            {
                _peerSpeakingStates[senderId] = false;
                OnPeerSpeakingStateChanged?.Invoke(senderId, false);
                OnPeerVoiceStateUpdated?.Invoke(senderId, false, false);
            }
            return;
        }

        // Trigger speaking glow indicator
        if (!_peerSpeakingStates.TryGetValue(senderId, out bool wasSpeaking) || !wasSpeaking)
        {
            _peerSpeakingStates[senderId] = true;
            OnPeerSpeakingStateChanged?.Invoke(senderId, true);
            OnPeerVoiceStateUpdated?.Invoke(senderId, true, false);
        }

        // Decode Opus payload
        IOpusVoiceCodec decoder = _peerDecoders.GetOrAdd(senderId, static _ => new OpusVoiceCodec());

        // Check for dropped packets and apply Packet Loss Concealment (PLC)
        if (_peerLastSequence.TryGetValue(senderId, out ushort lastSeq))
        {
            ushort expectedSeq = unchecked((ushort)(lastSeq + 1));
            if (packet.Sequence > expectedSeq && (packet.Sequence - expectedSeq) < 5)
            {
                int droppedFrames = packet.Sequence - expectedSeq;
                for (int i = 0; i < droppedFrames; i++)
                {
                    short[] plcBuffer = new short[decoder.FrameSizeSamples];
                    decoder.Decode(ReadOnlySpan<byte>.Empty, plcBuffer, decodeFec: true);
                    AudioSoftLimiter.ProcessFrameInPlace(plcBuffer, GainDb);
                    PlaybackRegulator.Push(plcBuffer);
                    AudioEngine?.SubmitPlaybackPcm(plcBuffer);

                    OnVoiceFrameReceived?.Invoke(new ReceivedVoiceFrame(
                        senderId,
                        packet.ChannelType,
                        packet.ChannelId,
                        unchecked((ushort)(lastSeq + 1 + i)),
                        plcBuffer));
                }
            }
        }
        _peerLastSequence[senderId] = packet.Sequence;

        short[] pcmDecoded = new short[decoder.FrameSizeSamples];
        int samples = decoder.Decode(packet.Payload.Span, pcmDecoded);

        short[] limitedSamples = new short[samples];
        pcmDecoded.AsSpan(0, samples).CopyTo(limitedSamples);
        AudioSoftLimiter.ProcessFrameInPlace(limitedSamples, GainDb);
        PlaybackRegulator.Push(limitedSamples);
        AudioEngine?.SubmitPlaybackPcm(limitedSamples);

        OnVoiceFrameReceived?.Invoke(new ReceivedVoiceFrame(
            senderId,
            packet.ChannelType,
            packet.ChannelId,
            packet.Sequence,
            limitedSamples));
    }

    private async Task SilenceSweepLoopAsync(CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100), _timeProvider);
        try
        {
            while (!cancellationToken.IsCancellationRequested &&
                   await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                long nowTicks = Environment.TickCount64;
                foreach (KeyValuePair<uint, long> pair in _peerLastSeenTicks)
                {
                    uint senderId = pair.Key;
                    long lastSeen = pair.Value;
                    if (nowTicks - lastSeen > 350) // 350ms of silence turns off speaking indicator
                    {
                        if (_peerSpeakingStates.TryGetValue(senderId, out bool isSpeaking) && isSpeaking)
                        {
                            _peerSpeakingStates[senderId] = false;
                            OnPeerSpeakingStateChanged?.Invoke(senderId, false);
                            OnPeerVoiceStateUpdated?.Invoke(senderId, false, false);
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            System.Diagnostics.Debug.WriteLine($"[VoiceClientService] Silence sweep cancelled: {ex.Message}");
        }
    }

    private void StopAudioEngineSafely()
    {
        if (AudioEngine != null)
        {
            try
            {
                AudioEngine.OnPcmFrameCaptured -= OnAudioEngineFrameCaptured;
                AudioEngine.Stop();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VoiceClientService] AudioEngine stop notice: {ex.Message}");
            }
        }
    }

    private void DisposePeerDecodersSafely()
    {
        foreach (KeyValuePair<uint, IOpusVoiceCodec> pair in _peerDecoders)
        {
            try
            {
                pair.Value.Dispose();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VoiceClientService] Peer decoder dispose notice: {ex.Message}");
            }
        }
        _peerDecoders.Clear();
    }

    public async Task StopAsync()
    {
        StopAudioEngineSafely();

        CancellationTokenSource? cts = Interlocked.Exchange(ref _receiveCts, null);
        Socket? socket = Interlocked.Exchange(ref _udpSocket, null);
        Task? receiveTask = Interlocked.Exchange(ref _receiveTask, null);
        Task? sweepTask = Interlocked.Exchange(ref _silenceSweepTask, null);

        if (cts != null)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VoiceClientService] StopAsync CTS already disposed: {ex.Message}");
            }
        }

        // Dispose UDP socket BEFORE awaiting receive loop so pending WinSock ReceiveFromAsync aborts immediately
        socket?.Dispose();

        List<Task> pendingTasks = new List<Task>(2);
        if (receiveTask != null)
        {
            pendingTasks.Add(receiveTask);
        }
        if (sweepTask != null)
        {
            pendingTasks.Add(sweepTask);
        }

        if (pendingTasks.Count > 0)
        {
            try
            {
                Task allTasks = Task.WhenAll(pendingTasks);
                Task timeoutTask = Task.Delay(TimeSpan.FromMilliseconds(500));
                await Task.WhenAny(allTasks, timeoutTask).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VoiceClientService] Background loop stop notice: {ex.Message}");
            }
        }

        cts?.Dispose();
        DisposePeerDecodersSafely();
    }

    public async ValueTask DisposeAsync()
    {
        if (!_isDisposed)
        {
            _isDisposed = true;
            await StopAsync().ConfigureAwait(false);
            _noiseSuppressor.Dispose();
            _encoderCodec.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    public void Dispose()
    {
        if (!_isDisposed)
        {
            _isDisposed = true;
            StopAudioEngineSafely();

            CancellationTokenSource? cts = Interlocked.Exchange(ref _receiveCts, null);
            Socket? socket = Interlocked.Exchange(ref _udpSocket, null);
            _ = Interlocked.Exchange(ref _receiveTask, null);
            _ = Interlocked.Exchange(ref _silenceSweepTask, null);

            if (cts != null)
            {
                try
                {
                    cts.Cancel();
                }
                catch (ObjectDisposedException ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[VoiceClientService] Dispose CTS already disposed: {ex.Message}");
                }
                cts.Dispose();
            }

            socket?.Dispose();
            DisposePeerDecodersSafely();
            _noiseSuppressor.Dispose();
            _encoderCodec.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
