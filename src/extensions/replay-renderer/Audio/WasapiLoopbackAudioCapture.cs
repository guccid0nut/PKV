// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: WasapiLoopbackAudioCapture.cs
// System: NEXUS Replay Video Renderer Service
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging;

namespace ReplayRenderer.Service.Audio;

/// <summary>
///     Captures bit-perfect system audio output via WASAPI loopback capture on Windows
///     and streams synchronized 48kHz stereo PCM blocks over a dedicated Named Pipe to FFmpeg.
/// </summary>
public sealed class WasapiLoopbackAudioCapture(
    ILogger<WasapiLoopbackAudioCapture> logger,
    AudioFrameSyncBuffer syncBuffer,
    string pipeName) : IAsyncDisposable
{
    private readonly ILogger<WasapiLoopbackAudioCapture> _logger = logger;
    private NamedPipeServerStream? _audioPipeStream;
    private readonly TaskCompletionSource _connectedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _cts;
    private bool _disposed;
    private Task? _streamTask;
    private Task? _wasapiCaptureTask;

    public string PipeName { get; } = pipeName;

    public string PipePath => $@"\\.\pipe\{PipeName}";
    public AudioFrameSyncBuffer SyncBuffer { get; } = syncBuffer;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        _disposed = true;
        _logger.LogInformation("[WasapiAudio] Disposing WASAPI Audio Capture Service...");

        if (_cts != null)
        {
            _cts.Cancel();

            if (_wasapiCaptureTask != null)
                try
                {
                    await _wasapiCaptureTask;
                }
                catch
                {
                    // Suppress cancellation exception
                }

            if (_streamTask != null)
                try
                {
                    await _streamTask;
                }
                catch
                {
                    // Suppress cancellation exception
                }

            _cts.Dispose();
            _cts = null;
        }

        if (_audioPipeStream != null)
        {
            await _audioPipeStream.DisposeAsync();
            _audioPipeStream = null;
        }
    }

    /// <summary>
    ///     Starts the Named Pipe audio server and kicks off the background audio capture and streaming loops.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[WasapiAudio] Creating Named Pipe server for Audio Stream: {PipePath}", PipePath);

        if (OperatingSystem.IsWindows())
        {
            PipeSecurity pipeSecurity = new();
            pipeSecurity.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
                AccessControlType.Allow));

            _audioPipeStream = NamedPipeServerStreamAcl.Create(
                PipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                16 * 1024 * 1024,
                16 * 1024 * 1024,
                pipeSecurity);
        }
        else
        {
            _audioPipeStream = new NamedPipeServerStream(
                PipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                16 * 1024 * 1024,
                16 * 1024 * 1024);
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // 1. Start hardware WASAPI loopback capture worker (if Windows)
        if (OperatingSystem.IsWindows())
            _wasapiCaptureTask = Task.Run(() => WasapiLoopbackCaptureWorkerAsync(_cts.Token), CancellationToken.None);

        // 2. Await FFmpeg connection in background
        _streamTask = Task.Run(() => WaitForClientConnectionAsync(_cts.Token), CancellationToken.None);

        return Task.CompletedTask;
    }

    private async Task WasapiLoopbackCaptureWorkerAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[WasapiAudio] Initializing Windows WASAPI loopback capture device...");

        IMMDevice? device = null;
        IAudioClient? audioClient = null;
        IAudioCaptureClient? captureClient = null;
        nint formatPtr = nint.Zero;

        try
        {
            IMMDeviceEnumerator enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            int hr = enumerator.GetDefaultAudioEndpoint(eRender, eMultimedia, out device);
            if (hr != 0 || device == null)
            {
                _logger.LogWarning(
                    "[WasapiAudio] No active default multimedia audio endpoint found (HRESULT: 0x{Hr:X}). Operating in synthetic audio mode.",
                    hr);
                return;
            }

            Guid audioClientGuid = IID_IAudioClient;
            hr = device.Activate(ref audioClientGuid, 1, nint.Zero, out object audioClientObj);
            if (hr != 0 || audioClientObj == null)
            {
                _logger.LogWarning("[WasapiAudio] Failed to activate IAudioClient (HRESULT: 0x{Hr:X}).", hr);
                return;
            }

            audioClient = (IAudioClient)audioClientObj;
            hr = audioClient.GetMixFormat(out formatPtr);
            if (hr != 0 || formatPtr == nint.Zero)
            {
                _logger.LogWarning("[WasapiAudio] GetMixFormat failed (HRESULT: 0x{Hr:X}).", hr);
                return;
            }

            WAVEFORMATEX mixFormat = Marshal.PtrToStructure<WAVEFORMATEX>(formatPtr);
            _logger.LogInformation(
                "[WasapiAudio] Endpoint mix format: {Rate}Hz, {Channels}ch, {Bits}bit, FormatTag=0x{Tag:X}.",
                mixFormat.nSamplesPerSec, mixFormat.nChannels, mixFormat.wBitsPerSample, mixFormat.wFormatTag);

            // Request 100ms buffer with AUDCLNT_STREAMFLAGS_LOOPBACK
            long hnsBufferDuration = 1000000; // 100ms in 100ns units
            Guid emptySession = Guid.Empty;
            hr = audioClient.Initialize(AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_LOOPBACK, hnsBufferDuration, 0,
                formatPtr, ref emptySession);
            if (hr != 0)
            {
                _logger.LogWarning("[WasapiAudio] IAudioClient::Initialize failed with HRESULT 0x{Hr:X}.", hr);
                return;
            }

            Guid captureClientGuid = IID_IAudioCaptureClient;
            hr = audioClient.GetService(ref captureClientGuid, out object captureClientObj);
            if (hr != 0 || captureClientObj == null)
            {
                _logger.LogWarning(
                    "[WasapiAudio] IAudioClient::GetService(IAudioCaptureClient) failed with HRESULT 0x{Hr:X}.", hr);
                return;
            }

            captureClient = (IAudioCaptureClient)captureClientObj;
            hr = audioClient.Start();
            if (hr != 0)
            {
                _logger.LogWarning("[WasapiAudio] IAudioClient::Start failed with HRESULT 0x{Hr:X}.", hr);
                return;
            }

            _logger.LogInformation("[WasapiAudio] WASAPI loopback capture successfully started.");

            // Capture loop polling at 5ms intervals
            while (!cancellationToken.IsCancellationRequested)
            {
                hr = captureClient.GetNextPacketSize(out uint packetFrames);
                while (hr == 0 && packetFrames > 0 && !cancellationToken.IsCancellationRequested)
                {
                    hr = captureClient.GetBuffer(out nint pData, out uint numFramesRead, out uint flags,
                        out ulong devPos, out ulong qpcPos);
                    if (hr == 0 && numFramesRead > 0 && pData != nint.Zero)
                    {
                        int outByteLength = (int)(numFramesRead * 4); // 2 channels * 2 bytes (16-bit)
                        byte[] pcmData = new byte[outByteLength];

                        if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0)
                        {
                            Array.Clear(pcmData, 0, outByteLength);
                        }
                        else
                        {
                            bool isFloat = mixFormat.wBitsPerSample == 32;
                            int srcChannels = mixFormat.nChannels;

                            if (isFloat)
                            {
                                int floatCount = (int)(numFramesRead * srcChannels);
                                float[] floatBuf = new float[floatCount];
                                Marshal.Copy(pData, floatBuf, 0, floatCount);

                                int outIdx = 0;
                                for (int i = 0; i < (int)numFramesRead; i++)
                                {
                                    float left = floatBuf[i * srcChannels + 0];
                                    float right = srcChannels > 1 ? floatBuf[i * srcChannels + 1] : left;

                                    short sLeft = (short)Math.Clamp((int)(left * 32767.0f), -32768, 32767);
                                    short sRight = (short)Math.Clamp((int)(right * 32767.0f), -32768, 32767);

                                    pcmData[outIdx++] = (byte)(sLeft & 0xFF);
                                    pcmData[outIdx++] = (byte)((sLeft >> 8) & 0xFF);
                                    pcmData[outIdx++] = (byte)(sRight & 0xFF);
                                    pcmData[outIdx++] = (byte)((sRight >> 8) & 0xFF);
                                }
                            }
                            else if (srcChannels == 2 && mixFormat.wBitsPerSample == 16)
                            {
                                Marshal.Copy(pData, pcmData, 0, outByteLength);
                            }
                            else
                            {
                                Array.Clear(pcmData, 0, outByteLength);
                            }
                        }

                        SyncBuffer.TryEnqueueAudio(pcmData);
                        captureClient.ReleaseBuffer(numFramesRead);
                    }

                    hr = captureClient.GetNextPacketSize(out packetFrames);
                }

                await Task.Delay(5, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[WasapiAudio] WASAPI capture loop canceled cleanly.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[WasapiAudio] Exception in WASAPI loopback capture: {Message}. Switching to silence fallback.",
                ex.Message);
        }
        finally
        {
            if (audioClient != null)
                try
                {
                    audioClient.Stop();
                }
                catch
                {
                    // Suppress cleanup error
                }

            if (formatPtr != nint.Zero) Marshal.FreeCoTaskMem(formatPtr);
        }
    }

    public bool IsConnected => _audioPipeStream != null && _audioPipeStream.IsConnected;

    /// <summary>
    ///     Writes exactly one audio frame block (e.g. 3,200 bytes for 48kHz stereo 60fps)
    ///     synchronized 1:1 with video frames.
    /// </summary>
    public async ValueTask WriteAudioFrameAsync(CancellationToken cancellationToken)
    {
        if (_audioPipeStream == null) return;

        if (!_audioPipeStream.IsConnected)
        {
            try
            {
                using CancellationTokenSource waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                waitCts.CancelAfter(TimeSpan.FromSeconds(5));
                await _connectedTcs.Task.WaitAsync(waitCts.Token);
            }
            catch
            {
                if (!_audioPipeStream.IsConnected)
                {
                    SyncBuffer.GetNextFrameAudioBlock();
                    return;
                }
            }
        }

        byte[] audioFrame = SyncBuffer.GetNextFrameAudioBlock();
        await _audioPipeStream.WriteAsync(audioFrame.AsMemory(0, audioFrame.Length), cancellationToken);
    }

    /// <summary>
    ///     Flushes the audio pipe and blocks until FFmpeg has read all buffered audio data, up to the specified timeout.
    /// </summary>
    public async Task WaitForDrainAsync(TimeSpan timeout)
    {
        if (_audioPipeStream != null && _audioPipeStream.IsConnected)
        {
            try
            {
                _logger.LogInformation("[WasapiAudio] Awaiting audio pipe drain by FFmpeg encoder...");
                if (OperatingSystem.IsWindows())
                {
                    using CancellationTokenSource cts = new(timeout);
                    await Task.Run(() =>
                    {
                        if (OperatingSystem.IsWindows()) _audioPipeStream.WaitForPipeDrain();
                    }).WaitAsync(cts.Token);
                }
                _logger.LogInformation("[WasapiAudio] Audio pipe drained successfully by FFmpeg.");
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("[WasapiAudio] Audio pipe drain timed out after {Timeout}s.", timeout.TotalSeconds);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[WasapiAudio] Audio pipe drain ended: {Message}", ex.Message);
            }
        }
    }

    private async Task WaitForClientConnectionAsync(CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("[WasapiAudio] Awaiting FFmpeg connection on audio pipe: {PipePath}...", PipePath);
            await _audioPipeStream!.WaitForConnectionAsync(cancellationToken);
            _connectedTcs.TrySetResult();
            _logger.LogInformation("[WasapiAudio] FFmpeg connected to audio pipe successfully.");
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[WasapiAudio] Audio pipe connection wait canceled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[WasapiAudio] Exception awaiting audio pipe connection: {Message}", ex.Message);
        }
    }

    #region COM P/Invoke Definitions for WASAPI Loopback

    private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IID_IMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    private static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

    private const int eRender = 0;
    private const int eMultimedia = 1;
    private const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    private const uint AUDCLNT_SHAREMODE_SHARED = 0;
    private const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x00000002;

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject
    {
    }

    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out nint ppDevices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppDevice);

        int GetDevice(string pwstrId, out IMMDevice ppDevice);
        int RegisterEndpointNotificationCallback(nint pClient);
        int UnregisterEndpointNotificationCallback(nint pClient);
    }

    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, int dwClsCtx, nint pActivationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);

        int OpenPropertyStore(int stgmAccess, out nint ppProperties);
        int GetId(out nint ppstrId);
        int GetState(out int pdwState);
    }

    [Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig]
        int Initialize(uint shareMode, uint streamFlags, long hnsBufferDuration, long hnsPeriodicity, nint pFormat,
            ref Guid audioSessionGuid);

        [PreserveSig]
        int GetBufferSize(out uint pNumBufferFrames);

        [PreserveSig]
        int GetStreamLatency(out long phnsLatency);

        [PreserveSig]
        int GetCurrentPadding(out uint pNumPaddingFrames);

        [PreserveSig]
        int IsFormatSupported(uint shareMode, nint pFormat, out nint ppClosestMatch);

        [PreserveSig]
        int GetMixFormat(out nint ppDeviceFormat);

        [PreserveSig]
        int GetDevicePeriod(out long phnsDefaultDevicePeriod, out long phnsMinimumDevicePeriod);

        [PreserveSig]
        int Start();

        [PreserveSig]
        int Stop();

        [PreserveSig]
        int Reset();

        [PreserveSig]
        int SetEventHandle(nint eventHandle);

        [PreserveSig]
        int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    }

    [Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig]
        int GetBuffer(out nint ppData, out uint pNumFramesToRead, out uint pdwFlags, out ulong pu64DevicePosition,
            out ulong pu64QPCPosition);

        [PreserveSig]
        int ReleaseBuffer(uint numFramesRead);

        [PreserveSig]
        int GetNextPacketSize(out uint pNumFramesInNextPacket);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    #endregion
}
