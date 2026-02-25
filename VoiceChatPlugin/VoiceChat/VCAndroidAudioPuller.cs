#if ANDROID
using Il2CppInterop.Runtime.Injection;
using Interstellar.VoiceChat;
using NAudio.Wave;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

/// <summary>
/// Android 音频播放器：每帧从 ManualSpeaker (ISampleProvider) 拉取解码后的 PCM，
/// 写入环形 AudioClip 由 AudioSource 循环播放。
///
/// 声道数和采样率从 <see cref="ISampleProvider.WaveFormat"/> 读取，
/// 在首次 Read 返回有效数据后延迟初始化 AudioClip，
/// 以确保 Interstellar RTC 已完成格式协商。
/// </summary>
public class VCAndroidAudioPuller : MonoBehaviour
{
    // ── 静态注册（仅一次）────────────────────────────────────────────
    private static bool _registered;

    public static void EnsureRegistered()
    {
        if (_registered) return;
        _registered = true;
        try
        {
            ClassInjector.RegisterTypeInIl2Cpp<VCAndroidAudioPuller>();
            VoiceChatPluginMain.Logger.LogInfo("[VCAndroidAudioPuller] Registered.");
        }
        catch (Exception ex)
        {
            VoiceChatPluginMain.Logger.LogWarning(
                $"[VCAndroidAudioPuller] Register warning: {ex.Message}");
        }
    }

    // IL2CPP 必须提供此构造函数
    public VCAndroidAudioPuller(IntPtr ptr) : base(ptr) { }

    // ── 实例字段 ─────────────────────────────────────────────────────
    private ISampleProvider? _provider;
    private AudioSource?     _audioSource;

    // 环形缓冲 — 在格式确定后才创建
    private AudioClip? _ringClip;
    private float[]?   _readBuf;
    private int        _writePos;

    // 从 WaveFormat 读到的格式参数
    private int _sampleRate;
    private int _channels;
    private int _clipFrames;   // 环形缓冲帧数（约 0.5 秒）

    // 是否已经完成延迟初始化
    private bool _initialized;

    // ── 公开初始化入口（由 VoiceChatRoom 在主线程调用）──────────────
    public void Init(ManualSpeaker speaker)
    {
        if (speaker == null)
        {
            VoiceChatPluginMain.Logger.LogError("[VCAndroidAudioPuller] Init: speaker is null");
            return;
        }

        // ManualSpeaker 是纯托管类，直接 as 转型
        _provider = speaker as ISampleProvider;
        if (_provider == null)
        {
            VoiceChatPluginMain.Logger.LogError(
                "[VCAndroidAudioPuller] ManualSpeaker does not implement ISampleProvider");
            return;
        }

        _audioSource = GetComponent<AudioSource>();
        if (_audioSource == null)
        {
            VoiceChatPluginMain.Logger.LogError("[VCAndroidAudioPuller] Missing AudioSource");
            return;
        }

        VoiceChatPluginMain.Logger.LogInfo("[VCAndroidAudioPuller] Init OK, waiting for WaveFormat...");
    }

    // ── 每帧拉取 PCM ─────────────────────────────────────────────────
    private void LateUpdate()
    {
        if (_provider == null || _audioSource == null) return;

        try
        {
            // ── 格式延迟初始化 ─────────────────────────────────────
            // 首次调用时 WaveFormat 可能还是 null（RTC 尚未协商完），
            // 每帧尝试直到 WaveFormat 可用且合法。
            if (!_initialized)
            {
                var wf = _provider.WaveFormat;
                if (wf == null || wf.SampleRate <= 0 || wf.Channels <= 0)
                    return;  // 还没准备好，下一帧再试

                _sampleRate = wf.SampleRate;
                _channels   = wf.Channels;
                _clipFrames = _sampleRate / 2;   // 0.5 秒环形缓冲
                _readBuf    = new float[(_sampleRate / 50) * _channels];  // 20ms 每帧

                _ringClip = AudioClip.Create(
                    "VC_RingBuf", _clipFrames, _channels, _sampleRate, false);

                _audioSource.clip         = _ringClip;
                _audioSource.loop         = true;
                _audioSource.volume       = 1f;
                _audioSource.spatialBlend = 0f;
                _audioSource.playOnAwake  = false;
                _audioSource.Play();

                _writePos    = 0;
                _initialized = true;

                VoiceChatPluginMain.Logger.LogInfo(
                    $"[VCAndroidAudioPuller] AudioClip created: " +
                    $"{_sampleRate}Hz {_channels}ch, {_clipFrames} frames");
            }

            // ── 每帧拉取约 20ms 的 PCM ────────────────────────────
            if (_ringClip == null || _readBuf == null) return;

            // 20ms 帧：sampleRate/50 per channel
            int framesPerUpdate = _sampleRate / 50;
            int floatCount      = framesPerUpdate * _channels;

            // 若 readBuf 比需要的小则扩容（正常情况不会发生）
            if (_readBuf.Length < floatCount)
                _readBuf = new float[floatCount];

            int read = _provider.Read(_readBuf, 0, floatCount);
            if (read <= 0) return;

            int framesRead = read / _channels;
            WriteRing(framesRead);
        }
        catch (Exception ex)
        {
            VoiceChatPluginMain.Logger.LogError(
                $"[VCAndroidAudioPuller] LateUpdate error: {ex.Message}");
        }
    }

    // ── 写入环形缓冲 ─────────────────────────────────────────────────
    private void WriteRing(int framesRead)
    {
        if (_ringClip == null || _readBuf == null || framesRead <= 0) return;

        int remaining = _clipFrames - _writePos;

        if (framesRead <= remaining)
        {
            // 整段写入
            var seg = new float[framesRead * _channels];
            Array.Copy(_readBuf, 0, seg, 0, framesRead * _channels);
            _ringClip.SetData(seg, _writePos);
            _writePos = (_writePos + framesRead) % _clipFrames;
        }
        else
        {
            // 跨边界写入：分两段
            var seg1 = new float[remaining * _channels];
            Array.Copy(_readBuf, 0, seg1, 0, remaining * _channels);
            _ringClip.SetData(seg1, _writePos);

            int second = framesRead - remaining;
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
        _initialized = false;
    }
}
#endif
