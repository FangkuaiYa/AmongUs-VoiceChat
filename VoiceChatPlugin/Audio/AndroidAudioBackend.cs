#if ANDROID
using System;
using System.Collections.Concurrent;
using UnityEngine;
using Concentus;

namespace VoiceChatPlugin.Audio;

/// <summary>
/// Android back-end for microphone capture.
/// Uses UnityEngine.Microphone instead of NAudio WaveInEvent.
/// Mirrors the WaveInEvent API surface so AudioRouting.cs can wrap it
/// with minimal ifdefs.
/// </summary>
public sealed class AndroidMicrophoneInput : IDisposable
{
    public const int SampleRate   = 48000;
    public const int Channels     = 1;
    public const int ClipSeconds  = 2;   // ring-buffer length

    private AudioClip?   _clip;
    private int          _lastPos;
    private bool         _recording;
    private float        _volume = 1f;

    // Callback matches NAudio's WaveInEventArgs pattern.
    public event Action<float[], int>? DataAvailable;

    public string DeviceName { get; private set; } = "";

    public void SetDevice(string name)
    {
        bool wasRunning = _recording;
        if (wasRunning) Stop();
        DeviceName = name ?? "";
        if (wasRunning) Start();
    }

    public void SetVolume(float v) => _volume = Math.Clamp(v, 0f, 4f);

    public void Start()
    {
        // Nebula pattern: never pass null to Microphone.Start on Android IL2CPP.
        // Fall back to the first enumerated device when no device is configured.
        string device = string.IsNullOrEmpty(DeviceName)
            ? (Microphone.devices.Length > 0 ? Microphone.devices[0] : "")
            : DeviceName;
        DeviceName = device;
        _clip      = Microphone.Start(device, true, ClipSeconds, SampleRate);
        _lastPos   = 0;
        _recording = true;
    }

    public void Stop()
    {
        _recording = false;
        if (!string.IsNullOrEmpty(DeviceName))
            Microphone.End(DeviceName);
        _clip = null;
    }

    /// <summary>
    /// Must be called every frame from the Unity main thread.
    /// Drains newly written microphone samples and fires DataAvailable.
    /// </summary>
    public void Tick()
    {
        if (!_recording || _clip == null) return;

        int pos = Microphone.GetPosition(string.IsNullOrEmpty(DeviceName) ? "" : DeviceName);
        if (pos < 0) return;

        int newSamples = pos >= _lastPos
            ? pos - _lastPos
            : (_clip.samples - _lastPos) + pos;

        if (newSamples <= 0) return;

        var buf = new float[newSamples];
        _clip.GetData(buf, _lastPos % _clip.samples);
        _lastPos = pos;

        if (_volume != 1f)
            for (int i = 0; i < buf.Length; i++) buf[i] *= _volume;

        DataAvailable?.Invoke(buf, buf.Length);
    }

    public void Dispose() => Stop();

    // ── Device enumeration ─────────────────────────────────────────────────

    public static string[] GetDeviceNames() => Microphone.devices;
}

/// <summary>
/// Android back-end for audio playback.
/// Uses UnityEngine.AudioSource with a streaming AudioClip instead of WasapiOut.
/// </summary>
public sealed class AndroidAudioOutput : IDisposable
{
    private readonly AudioSource   _source;
    private readonly AudioClip     _clip;
    private readonly int           _bufferSamples;
    private readonly float[]       _sampleBuf;
    private readonly ConcurrentQueue<float[]> _pendingChunks = new();

    private int   _writePos;
    private float _volume = 1f;

    // 200 ms ring buffer (stereo, 48 kHz)
    private const int BufferMs      = 200;
    private const int TargetSR      = 48000;
    private const int TargetChans   = 2;

    public AndroidAudioOutput(GameObject hostObject)
    {
        _bufferSamples = TargetSR * TargetChans * BufferMs / 1000;
        _sampleBuf     = new float[_bufferSamples];

		_clip = AudioClip.Create(
			"VC_Output",
			TargetSR * TargetChans * BufferMs / 1000 / TargetChans,
			TargetChans,
			TargetSR,
			true,
			(AudioClip.PCMReaderCallback)((ary) => FillBuffer(ary)));
		
        _source = hostObject.AddComponent<AudioSource>();
        _source.clip   = _clip;
        _source.loop   = true;
        _source.volume = 1f;
        _source.Play();
    }

    private void FillBuffer(float[] data)
    {
        // Drain pending decoded chunks into data[]
        int filled = 0;
        while (filled < data.Length)
        {
            if (!_pendingChunks.TryPeek(out var chunk)) break;

            int needed = data.Length - filled;
            int avail  = chunk.Length - (_writePos % chunk.Length);

            if (avail <= needed)
            {
                _pendingChunks.TryDequeue(out _);
                Array.Copy(chunk, 0, data, filled, avail);
                filled   += avail;
                _writePos = 0;
            }
            else
            {
                int offset = _writePos % chunk.Length;
                Array.Copy(chunk, offset, data, filled, needed);
                _writePos += needed;
                filled     = data.Length;
            }
        }
        // Silence the rest
        if (filled < data.Length)
            Array.Clear(data, filled, data.Length - filled);

        if (_volume != 1f)
            for (int i = 0; i < data.Length; i++) data[i] *= _volume;
    }

    /// <summary>Enqueue a stereo PCM chunk (interleaved float, 48 kHz).</summary>
    public void EnqueueSamples(float[] samples) => _pendingChunks.Enqueue(samples);

    public void SetVolume(float v)
    {
        _volume        = Math.Clamp(v, 0f, 4f);
        _source.volume = Math.Clamp(v, 0f, 1f);  // AudioSource volume [0,1]
    }

    public void Dispose()
    {
        _source.Stop();
        if (_source != null) UnityEngine.Object.Destroy(_source);
    }
}
#endif
