using System;
using System.Threading;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Interstellar.Voice;
using UnityEngine;

namespace Interstellar.Android;

public class AndroidSpeaker : IDisposable
{
    private const int SampleRate = 48000;
    private const int Channels = 2;

    private readonly ManualSpeaker _manualSpeaker;

    private AudioSource? _audioSource;
    private AudioClip? _clip;
    private GameObject? _gameObject;

    private float[] _callbackScratch = Array.Empty<float>();

    private Action<Il2CppStructArray<float>>? _pcmManaged;
    private AudioClip.PCMReaderCallback? _pcmCb;
    private bool _started, _disposed;
    private float _diagTimer;
    private int _cbCount, _urCount, _lastCb, _lastUr;

    public ManualSpeaker Speaker => _manualSpeaker;

    public AndroidSpeaker()
    {
        _manualSpeaker = new ManualSpeaker(null);
    }

    public void Setup()
    {
        _gameObject = new GameObject("VC_AndroidSpeaker");
        UnityEngine.Object.DontDestroyOnLoad(_gameObject);

        _audioSource = _gameObject.AddComponent<AudioSource>();
        _audioSource.loop = true;
        _audioSource.volume = 1f;
        _audioSource.spatialBlend = 0f;
    }

    public void StartPlayback()
    {
        if (_started) return;
        // Create AudioClip AFTER Initialize so PCM callback never fires before speakerContext is set
        _pcmManaged = new Action<Il2CppStructArray<float>>(OnPcmRead);
        _pcmCb = DelegateSupport.ConvertDelegate<AudioClip.PCMReaderCallback>(_pcmManaged);
        if (_pcmCb == null)
            throw new InvalidOperationException("Failed to create IL2CPP PCM reader callback.");

        _clip = AudioClip.Create("VC_Out", SampleRate / 4, Channels, SampleRate, true, _pcmCb);
        _audioSource!.clip = _clip;
        _audioSource!.Play();
        _started = true;
        InterstellarPlugin.Logger.LogInfo("[VC:AndroidSpk] Started PCM callback speaker.");
    }

    /// <summary>Call early to ensure AudioSource is created and ready
    /// before the WebSocket connection completes.</summary>
    public void Warmup()
    {
        if (!_started && !_disposed && _gameObject != null)
        {
            StartPlayback();
        }
    }

    public void Update()
    {
        if (!_started || _disposed) return;

        _diagTimer += Time.unscaledDeltaTime;
        if (_diagTimer > 3f)
        {
            _diagTimer = 0f;
            int callbacks = Volatile.Read(ref _cbCount);
            int underruns = Volatile.Read(ref _urCount);
            int cb = callbacks - _lastCb;
            int ur = underruns - _lastUr;
            _lastCb = callbacks;
            _lastUr = underruns;
            InterstellarPlugin.Logger.LogInfo(
                $"[VC:AndroidSpk] cb+{cb} ur+{ur} totalUR={underruns}");
        }
    }

    private void OnPcmRead(Il2CppStructArray<float> data)
    {
        if (_callbackScratch.Length != data.Length)
        {
            _callbackScratch = new float[data.Length];
        }

        try
        {
            _manualSpeaker.Read(_callbackScratch);
        }
        catch
        {
            Interlocked.Increment(ref _urCount);
            Array.Clear(_callbackScratch, 0, _callbackScratch.Length);
        }

        // Android AudioTrack output is significantly quieter than desktop.
        // 2x gain brings speech to a usable level; Clamp prevents hard clipping.
        const float androidSpeakerGain = 2f;
        for (int i = 0; i < data.Length; i++)
        {
            float sample = _callbackScratch[i] * androidSpeakerGain;
            data[i] = float.IsFinite(sample) ? Math.Clamp(sample, -1f, 1f) : 0f;
        }

        Interlocked.Increment(ref _cbCount);
    }

    public void Stop()
    {
        if (_audioSource != null) { _audioSource.Stop(); _audioSource.clip = null; }
        if (_clip != null) { UnityEngine.Object.Destroy(_clip); _clip = null; }
        if (_gameObject != null) { UnityEngine.Object.Destroy(_gameObject); _gameObject = null; }
        _audioSource = null;
        _started = false;
        InterstellarPlugin.Logger.LogInfo("[VC:AndroidSpk] Stopped.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
