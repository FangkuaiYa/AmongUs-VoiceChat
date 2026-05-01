#if ANDROID
using System;
using NAudio.Wave;
using UnityEngine;
using VoiceChatPlugin.Audio;

namespace VoiceChatPlugin.VoiceChat;

/// <summary>
/// Android audio output backend.
///
/// Mirrors Nebula's NoSVCRoom.cs exactly:
///
///   var audioSource = ModSingleton&lt;ResidentBehaviour&gt;.Instance.gameObject.AddComponent&lt;AudioSource&gt;();
///   audioSource.MarkDontUnload();
///   var speaker = new ManualSpeaker(() => { if (audioSource) GameObject.Destroy(audioSource); });
///   AudioClip myClip = AudioClip.Create("VCAudio", (int)(sampleRate * 0.5f), 2, sampleRate, true,
///       (AudioClip.PCMReaderCallback)(ary => speaker.Read(ary)));
///   audioSource.clip = myClip;
///   audioSource.loop = true;
///   audioSource.Play();
///
/// Nebula's ManualSpeaker.Read(ary) is driven by Unity's audio thread via PCMReaderCallback.
/// In Nebula, ManualSpeaker internally calls _endpoint.Read() (the Interstellar audio graph
/// endpoint) to pull rendered PCM through the full volume/pan/effects routing graph.
///
/// We replicate this exactly: PCMReaderCallback calls _endpoint.Read() directly,
/// pulling audio through the full AudioManager routing graph (volume, stereo pan,
/// ghost reverb, radio filter, etc.) — not from a separate ring buffer.
///
/// This is the key fix: previously WriteMono() bypassed the graph entirely.
/// Now the PCMReaderCallback IS the graph's consumer, just like Nebula's ManualSpeaker.
/// </summary>
internal sealed class AndroidSpeaker : IDisposable
{
    // Match Nebula: (int)(interstellarRoom.SampleRate * 0.5f) samples, 2 channels
    private const int   SampleRate = 48000;
    private const int   Channels   = 2;
    private const float ClipSecs   = 0.5f;

    private readonly AudioSource      _source;
    private readonly AudioClip        _clip;
    private readonly ISampleProvider  _endpoint; // the AudioManager graph endpoint
    private readonly float[]          _readBuf;  // scratch buffer for mono→stereo conversion
    private float _masterVolume = 1f;

    public bool IsPlaying => _source != null && _source.isPlaying;

    /// <summary>
    /// Create the speaker. The <paramref name="endpoint"/> is the AudioManager.Endpoint
    /// (ISampleProvider) — the final output of the audio routing graph.
    /// We call endpoint.Read() inside the PCMReaderCallback to pull audio through
    /// the full graph, exactly as Nebula's ManualSpeaker does via Interstellar.
    /// </summary>
    public AndroidSpeaker(ISampleProvider endpoint)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));

        var host = VoiceChatPluginMain.ResidentObject
            ?? throw new InvalidOperationException("[VC] ResidentObject is null");

        int clipSamples = (int)(SampleRate * ClipSecs); // Nebula: sampleRate * 0.5f
        _readBuf = new float[clipSamples * Channels];

        // Add AudioSource to ResidentObject — Nebula: ResidentBehaviour.gameObject.AddComponent<AudioSource>()
        _source = host.AddComponent<AudioSource>();
        // Nebula: audioSource.MarkDontUnload()
        _source.hideFlags  |= HideFlags.DontUnloadUnusedAsset | HideFlags.HideAndDontSave;
        _source.spatialBlend = 0f; // 2D
        _source.volume       = 1f;

        // Nebula: AudioClip.Create("VCAudio", (int)(sampleRate * 0.5f), 2, sampleRate, true,
        //             (PCMReaderCallback)(ary => speaker.Read(ary)))
        _clip = AudioClip.Create(
            "VCAudio",
            clipSamples,
            Channels,
            SampleRate,
            true,
            (AudioClip.PCMReaderCallback)(ary => Read(ary)));

        _source.clip = _clip;
        _source.loop = true;
        _source.Play();

        VoiceChatPluginMain.Logger.LogInfo("[VC] Android speaker initialised (Nebula pattern, graph-driven).");
    }

    // ── PCMReaderCallback — called by Unity audio thread ────────────────────
    // Mirrors Nebula's ManualSpeaker.Read(ary):
    // pulls audio through the full AudioManager routing graph via _endpoint.Read().

    private void Read(float[] data)
    {
        // _endpoint outputs stereo at 48 kHz — same format as the AudioClip.
        int got = _endpoint.Read(data, 0, data.Length);

        // Zero any unfilled samples (under-run)
        for (int i = got; i < data.Length; i++) data[i] = 0f;

        // Apply master volume
        if (_masterVolume != 1f)
            for (int i = 0; i < data.Length; i++) data[i] *= _masterVolume;
    }

    // ── Volume ────────────────────────────────────────────────────────────────

    public void SetMasterVolume(float v)
    {
        _masterVolume  = Math.Clamp(v, 0f, 2f);
        _source.volume = Math.Clamp(v, 0f, 1f);
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public void Dispose()
    {
        _source.Stop();
        // Nebula: if (audioSource) GameObject.Destroy(audioSource)
        if (_source != null) UnityEngine.Object.Destroy(_source);
        VoiceChatPluginMain.Logger.LogInfo("[VC] Android speaker disposed.");
    }
}
#endif
