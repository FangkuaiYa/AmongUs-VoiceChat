#if ANDROID
using System;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

/// <summary>
/// Android microphone bridge.
///
/// On Android, NAudio's WaveInEvent is unavailable. Instead we use Unity's
/// built-in <see cref="Microphone"/> API to capture audio, then feed the raw
/// PCM float samples into the same Opus-encode pipeline used on Windows.
///
/// This mirrors the approach used in Nebula (NoSVCRoom.cs) where a
/// ManualMicrophone + PushAudioData loop drives the voice-chat microphone on
/// the Android platform.
///
/// Threading:
///   <see cref="Poll"/> is called every frame from the Unity main thread.
///   The encoded packet is enqueued into the same ConcurrentQueue that the
///   Windows WaveInEvent callback uses, so the rest of <see cref="VoiceChatRoom"/>
///   requires no changes.
/// </summary>
internal sealed class AndroidMicrophone : IDisposable
{
    // ── Config ──────────────────────────────────────────────────────────────
    private const int SampleRate   = 48000;  // Hz — must match AudioHelpers.ClockRate
    private const int LoopSeconds  = 1;      // Unity looping clip length
    private const int FrameSamples = 960;    // 20 ms @ 48 kHz (one Opus frame)

    // ── State ────────────────────────────────────────────────────────────────
    private string?    _deviceName;
    private AudioClip? _clip;
    private int        _lastPosition;
    private bool       _started;
    private float[]    _readBuf = new float[FrameSamples];

    /// <summary>Whether the microphone is currently capturing.</summary>
    public bool IsCapturing => _started && _clip != null;

    /// <summary>Most recently measured peak level (0–1) for the VU meter.</summary>
    public float Level { get; private set; }

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Start capturing from the given device name.
    /// Pass <c>null</c> or empty string for the default device.
    /// Returns <c>true</c> on success.
    /// </summary>
    public bool Start(string? deviceName)
    {
        Stop();

        // Validate device exists; fall back to first available.
        if (string.IsNullOrEmpty(deviceName) || !IsDeviceAvailable(deviceName))
            deviceName = Microphone.devices.Length > 0 ? Microphone.devices[0] : null;

        if (deviceName == null)
        {
            VoiceChatPluginMain.Logger.LogWarning("[VC] Android: no microphone device found.");
            return false;
        }

        _deviceName   = deviceName;
        _clip         = Microphone.Start(deviceName, true, LoopSeconds, SampleRate);
        _lastPosition = 0;
        _started      = true;

        VoiceChatPluginMain.Logger.LogInfo($"[VC] Android mic started: '{deviceName}'");
        return true;
    }

    /// <summary>Stop the microphone.</summary>
    public void Stop()
    {
        if (_started && _deviceName != null)
        {
            Microphone.End(_deviceName);
            VoiceChatPluginMain.Logger.LogInfo($"[VC] Android mic stopped: '{_deviceName}'");
        }
        _clip       = null;
        _deviceName = null;
        _started    = false;
        Level       = 0f;
    }

    public void Dispose() => Stop();

    /// <summary>
    /// Call once per Unity frame (main thread).
    /// Reads any newly captured samples from the ring clip and invokes
    /// <paramref name="onSamples"/> for each full Opus frame (960 samples).
    ///
    /// <paramref name="onSamples"/> receives a <c>float[]</c> slice of exactly
    /// <see cref="FrameSamples"/> samples at <see cref="SampleRate"/> Hz mono.
    /// </summary>
    public void Poll(Action<float[], int> onSamples)
    {
        if (!IsCapturing || _clip == null) return;

        int currentPosition = Microphone.GetPosition(_deviceName);
        if (currentPosition < 0) return; // device error

        int available;
        if (currentPosition >= _lastPosition)
            available = currentPosition - _lastPosition;
        else
            available = _clip.samples - _lastPosition + currentPosition; // wrapped

        if (available <= 0) return;

        // Ensure read buffer is large enough
        if (_readBuf.Length < available)
            _readBuf = new float[available];

        // Unity's GetData takes the offset in samples (mono).
        _clip.GetData(_readBuf, _lastPosition);
        _lastPosition = currentPosition;

        // Measure peak level
        float peak = 0f;
        for (int i = 0; i < available; i++)
        {
            float abs = Math.Abs(_readBuf[i]);
            if (abs > peak) peak = abs;
        }
        Level = peak;

        // Dispatch full Opus frames
        int offset = 0;
        while (offset + FrameSamples <= available)
        {
            onSamples(_readBuf, offset);
            offset += FrameSamples;
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static bool IsDeviceAvailable(string name)
    {
        foreach (var d in Microphone.devices)
            if (d == name) return true;
        return false;
    }

    /// <summary>Return all available Android microphone device names.</summary>
    public static string[] GetDevices() => Microphone.devices;
}
#endif
