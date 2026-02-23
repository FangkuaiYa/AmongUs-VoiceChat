#if ANDROID
using BepInEx.Unity.IL2CPP.Utils.Collections;
using Il2CppInterop.Runtime.Injection;
using Interstellar.VoiceChat;
using NAudio.Wave;
using System.Collections;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

/// <summary>
/// Android IL2CPP 音频播放器。
///
/// 核心问题：在 BepInEx IL2CPP 环境下，通过 ClassInjector 注入的 MonoBehaviour
/// 不能依赖 Unity 的 OnAudioFilterRead 消息（Unity 原生层不知道这个托管方法）。
///
/// 解决方案（参考 Nebula 项目）：
///   1. 用 ClassInjector.RegisterTypeInIl2Cpp 注册类，使 Unity 能识别该组件。
///   2. 创建一个 PCM 静音 AudioClip 让 AudioSource 保持"播放"状态（触发音频线程）。
///   3. 用 BepInEx 协程每帧从 ManualSpeaker (ISampleProvider) 拉取解码后的 PCM，
///      写入一个 float[] 循环缓冲区，再通过 AudioSource.SetScheduledEndTime +
///      动态创建短 AudioClip 拼接播放（"streaming via coroutine"）。
///
/// 但实测 Nebula 中更简单的方式是：完全不用 AudioSource，
/// 而是将 ManualSpeaker 注册为 ISampleProvider，
/// 通过 Unity 的 AudioSettings.GetDSPBufferSize 获取帧大小后，
/// 在 LateUpdate 中用 AudioSource.clip.SetData 刷新环形缓冲。
///
/// 最终采用最稳健的方式：
///   - 静态注册（静态构造函数中 Register），避免重复注册崩溃
///   - 用固定大小环形 float[] 缓冲，每帧 LateUpdate 从 ISampleProvider 读取
///   - AudioClip 设置为 streaming=true 的环形clip，用 AudioClip.SetData 覆写
/// </summary>
public class VCAndroidAudioPuller : MonoBehaviour
{
    // ── 静态注册（只做一次）─────────────────────────────────────────
    private static bool _registered;

    public static void EnsureRegistered()
    {
        if (_registered) return;
        _registered = true;
        try
        {
            ClassInjector.RegisterTypeInIl2Cpp<VCAndroidAudioPuller>();
            VoiceChatPluginMain.Logger.LogInfo("[VCAndroidAudioPuller] Registered IL2CPP type.");
        }
        catch (Exception ex)
        {
            VoiceChatPluginMain.Logger.LogWarning($"[VCAndroidAudioPuller] Register warning (may already be registered): {ex.Message}");
        }
    }

    // ── 实例状态 ────────────────────────────────────────────────────
    private ISampleProvider? _provider;
    private AudioSource?     _audioSource;
    private AudioClip?       _ringClip;
    private float[]?         _readBuf;

    private int _clipSamples;   // clip 总帧数（单声道）
    private int _writePos;      // 当前写入位置（单声道帧索引）
    private int _sampleRate;

    // IL2CPP 要求注入类必须有接受 IntPtr 的构造函数
    public VCAndroidAudioPuller(IntPtr ptr) : base(ptr) { }

    /// <summary>
    /// 由 VoiceChatRoom 调用，传入 ManualSpeaker 和采样率。
    /// 必须在 Unity 主线程调用。
    /// </summary>
    public void Init(ManualSpeaker speaker, int sampleRate)
    {
        if (speaker == null)
        {
            VoiceChatPluginMain.Logger.LogError("[VCAndroidAudioPuller] Init: speaker is null.");
            return;
        }

        _provider   = (ISampleProvider)speaker;
        _sampleRate = sampleRate;

        // 2 声道输出，环形 clip 约 0.5 秒
        const int channels    = 2;
        _clipSamples          = sampleRate / 2;   // 每声道帧数
        int totalFloats       = _clipSamples * channels;
        _readBuf              = new float[totalFloats];

        // 创建环形 AudioClip（非 streaming 版，用 SetData 刷新）
        _ringClip = AudioClip.Create("VC_RingBuf", _clipSamples, channels, sampleRate, false);

        _audioSource = GetComponent<AudioSource>();
        if (_audioSource == null)
        {
            VoiceChatPluginMain.Logger.LogError("[VCAndroidAudioPuller] No AudioSource found.");
            return;
        }

        _audioSource.clip         = _ringClip;
        _audioSource.loop         = true;
        _audioSource.volume       = 1f;
        _audioSource.spatialBlend = 0f;
        _audioSource.playOnAwake  = false;
        _audioSource.Play();

        _writePos = 0;
        VoiceChatPluginMain.Logger.LogInfo($"[VCAndroidAudioPuller] Init OK: sampleRate={sampleRate} clipSamples={_clipSamples}");
    }

    // ── 每帧从 ISampleProvider 拉取新 PCM ──────────────────────────
    private void LateUpdate()
    {
        if (_provider == null || _audioSource == null || _ringClip == null || _readBuf == null)
            return;

        try
        {
            // 每帧拉取约 20ms 的数据（避免 buffer 堆积）
            int framesPerUpdate = _sampleRate / 50; // 20ms
            int channels        = _ringClip.channels;
            int floatCount      = framesPerUpdate * channels;

            // 确保缓冲区够大
            if (_readBuf.Length < floatCount)
                _readBuf = new float[floatCount];

            int read = _provider.Read(_readBuf, 0, floatCount);
            if (read <= 0) return;

            int framesRead = read / channels;

            // 写入 clip（环形）
            int remaining = _clipSamples - _writePos;
            if (framesRead <= remaining)
            {
                // 一次性写入
                var segment = new float[framesRead * channels];
                Array.Copy(_readBuf, 0, segment, 0, framesRead * channels);
                _ringClip.SetData(segment, _writePos);
                _writePos = (_writePos + framesRead) % _clipSamples;
            }
            else
            {
                // 分两段写（跨越 clip 尾部）
                int firstFrames  = remaining;
                int secondFrames = framesRead - firstFrames;

                var seg1 = new float[firstFrames * channels];
                Array.Copy(_readBuf, 0, seg1, 0, firstFrames * channels);
                _ringClip.SetData(seg1, _writePos);

                var seg2 = new float[secondFrames * channels];
                Array.Copy(_readBuf, firstFrames * channels, seg2, 0, secondFrames * channels);
                _ringClip.SetData(seg2, 0);

                _writePos = secondFrames;
            }
        }
        catch (Exception ex)
        {
            VoiceChatPluginMain.Logger.LogError($"[VCAndroidAudioPuller] LateUpdate error: {ex.Message}");
        }
    }

    private void OnDestroy()
    {
        _provider = null;
        if (_ringClip != null)
        {
            Destroy(_ringClip);
            _ringClip = null;
        }
    }
}
#endif
