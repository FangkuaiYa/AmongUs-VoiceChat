#if ANDROID
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Interstellar.VoiceChat;
using NAudio.Wave;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

/// <summary>
/// Android IL2CPP 音频播放器。
///
/// 核心修复：在 IL2CPP 中，C# 强转 (ISampleProvider)manualSpeaker 会抛出
/// InvalidCastException，必须使用 Il2Cpp 的 .TryCast&lt;T&gt;() 方法来获取接口代理。
///
/// 音频驱动方式：每帧 LateUpdate 从 ManualSpeaker 拉取 PCM，
/// 通过 AudioClip.SetData 写入环形 AudioClip，由 AudioSource 循环播放。
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
                $"[VCAndroidAudioPuller] Register warning (may already exist): {ex.Message}");
        }
    }

    // IL2CPP 注入类必须提供此构造函数
    public VCAndroidAudioPuller(IntPtr ptr) : base(ptr) { }

    // ── 实例字段 ────────────────────────────────────────────────────
    private ISampleProvider? _provider;   // 通过 TryCast 获取的接口代理
    private AudioSource?     _audioSource;
    private AudioClip?       _ringClip;

    private int      _clipSamples;        // 单声道帧数
    private int      _writePos;           // 写入光标（单声道帧索引）
    private int      _sampleRate;
    private int      _channels;
    private float[]? _readBuf;

    /// <summary>由 VoiceChatRoom 在主线程调用。</summary>
    public void Init(ManualSpeaker speaker, int sampleRate)
    {
        if (speaker == null)
        {
            VoiceChatPluginMain.Logger.LogError("[VCAndroidAudioPuller] Init: speaker is null.");
            return;
        }

        // ── 关键修复：用 TryCast 而非 C# 强转 ──────────────────────
        _provider = speaker.TryCast<ISampleProvider>();
        if (_provider == null)
        {
            VoiceChatPluginMain.Logger.LogError(
                "[VCAndroidAudioPuller] TryCast<ISampleProvider> failed. " +
                "ManualSpeaker may not expose ISampleProvider in this IL2CPP build.");
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

    // ── 每帧拉取 PCM 写入环形 AudioClip ────────────────────────────
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

            // 用 Il2CppStructArray 传递，避免 GC pinning 问题
            var il2Buf = new Il2CppStructArray<float>(floatCount);
            int read   = _provider.Read(il2Buf, 0, floatCount);
            if (read <= 0) return;

            // 拷回托管数组
            for (int i = 0; i < read; i++)
                _readBuf[i] = il2Buf[i];

            WriteRing(read / _channels);
        }
        catch (Exception ex)
        {
            VoiceChatPluginMain.Logger.LogError($"[VCAndroidAudioPuller] LateUpdate: {ex.Message}");
            // 一次错误不停止，继续下一帧尝试
        }
    }

    private void WriteRing(int framesRead)
    {
        if (_ringClip == null || _readBuf == null || framesRead <= 0) return;

        int remaining = _clipSamples - _writePos;
        if (framesRead <= remaining)
        {
            // 一段写入
            var seg = new float[framesRead * _channels];
            Array.Copy(_readBuf, 0, seg, 0, framesRead * _channels);
            _ringClip.SetData(seg, _writePos);
            _writePos = (_writePos + framesRead) % _clipSamples;
        }
        else
        {
            // 跨越 clip 尾部，分两段
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
