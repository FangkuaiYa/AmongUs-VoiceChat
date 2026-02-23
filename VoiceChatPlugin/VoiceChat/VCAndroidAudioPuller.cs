#if ANDROID
using Il2CppInterop.Runtime.Injection;
using Interstellar.VoiceChat;
using NAudio.Wave;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

/// <summary>
/// Android 音频播放器。
/// Interstellar.dll 是纯托管程序集，ManualSpeaker 是普通 C# 类，
/// 直接用 as ISampleProvider 转型即可，无需任何 IL2CPP TryCast。
/// </summary>
public class VCAndroidAudioPuller : MonoBehaviour
{
    // ── 静态注册（仅一次）───────────────────────────────────────────
    private static bool _registered;

    public static void EnsureRegistered()
    {
        if (_registered) return;
        _registered = true;
        try
        {
            ClassInjector.RegisterTypeInIl2Cpp<VCAndroidAudioPuller>();
            VoiceChatPluginMain.Logger.LogInfo("[VCAndroidAudioPuller] IL2CPP type registered.");
        }
        catch (Exception ex)
        {
            VoiceChatPluginMain.Logger.LogWarning(
                $"[VCAndroidAudioPuller] Register warning: {ex.Message}");
        }
    }

    // IL2CPP 注入类必须提供此构造函数
    public VCAndroidAudioPuller(IntPtr ptr) : base(ptr) { }

    // ── 实例字段 ────────────────────────────────────────────────────
    private ISampleProvider? _provider;
    private AudioSource?     _audioSource;
    private AudioClip?       _ringClip;
    private float[]?         _readBuf;

    private int _clipSamples;
    private int _writePos;
    private int _sampleRate;
    private int _channels;

    /// <summary>由 VoiceChatRoom 在主线程调用。</summary>
    public void Init(ManualSpeaker speaker, int sampleRate)
    {
        if (speaker == null)
        {
            VoiceChatPluginMain.Logger.LogError("[VCAndroidAudioPuller] Init: speaker is null.");
            return;
        }

        // Interstellar.dll 是普通托管 dll，ManualSpeaker 实现了 ISampleProvider，
        // 直接用 as 转型，不需要 TryCast。
        _provider = speaker as ISampleProvider;
        if (_provider == null)
        {
            VoiceChatPluginMain.Logger.LogError(
                "[VCAndroidAudioPuller] ManualSpeaker does not implement ISampleProvider.");
            return;
        }

        _sampleRate  = sampleRate;
        _channels    = 2;
        _clipSamples = sampleRate / 2;           // 约 0.5 秒环形缓冲
        _readBuf     = new float[_clipSamples * _channels];

        _ringClip = AudioClip.Create("VC_RingBuf", _clipSamples, _channels, sampleRate, false);

        _audioSource = GetComponent<AudioSource>();
        if (_audioSource == null)
        {
            VoiceChatPluginMain.Logger.LogError("[VCAndroidAudioPuller] Missing AudioSource.");
            return;
        }

        _audioSource.clip         = _ringClip;
        _audioSource.loop         = true;
        _audioSource.volume       = 1f;
        _audioSource.spatialBlend = 0f;
        _audioSource.playOnAwake  = false;
        _audioSource.Play();

        _writePos = 0;
        VoiceChatPluginMain.Logger.LogInfo(
            $"[VCAndroidAudioPuller] Init OK — sampleRate={sampleRate} clipSamples={_clipSamples}");
    }

    // ── 每帧从 ISampleProvider 拉取 PCM 写入环形 AudioClip ──────────
    private void LateUpdate()
    {
        if (_provider == null || _audioSource == null || _ringClip == null || _readBuf == null)
            return;

        try
        {
            // 每帧约拉取 20ms 的数据
            int framesPerUpdate = _sampleRate / 50;
            int floatCount      = framesPerUpdate * _channels;

            if (_readBuf.Length < floatCount)
                _readBuf = new float[floatCount];

            int read = _provider.Read(_readBuf, 0, floatCount);
            if (read <= 0) return;

            WriteRing(read / _channels);
        }
        catch (Exception ex)
        {
            VoiceChatPluginMain.Logger.LogError($"[VCAndroidAudioPuller] LateUpdate: {ex.Message}");
        }
    }

    private void WriteRing(int framesRead)
    {
        if (_ringClip == null || _readBuf == null || framesRead <= 0) return;

        int remaining = _clipSamples - _writePos;
        if (framesRead <= remaining)
        {
            var seg = new float[framesRead * _channels];
            Array.Copy(_readBuf, 0, seg, 0, framesRead * _channels);
            _ringClip.SetData(seg, _writePos);
            _writePos = (_writePos + framesRead) % _clipSamples;
        }
        else
        {
            int second = framesRead - remaining;

            var seg1 = new float[remaining * _channels];
            Array.Copy(_readBuf, 0, seg1, 0, remaining * _channels);
            _ringClip.SetData(seg1, _writePos);

            var seg2 = new float[second * _channels];
            Array.Copy(_readBuf, remaining * _channels, seg2, 0, second * _channels);
            _ringClip.SetData(seg2, 0);

            _writePos = second;
        }
    }

    private void OnDestroy()
    {
        _provider = null;
        if (_ringClip != null) { Destroy(_ringClip); _ringClip = null; }
    }
}
#endif
