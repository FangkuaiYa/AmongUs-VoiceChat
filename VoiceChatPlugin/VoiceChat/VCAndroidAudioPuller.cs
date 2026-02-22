#if ANDROID
using Interstellar.VoiceChat;
using NAudio.Wave;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

/// <summary>
/// IL2CPP/Android 环境下，AudioClip.PCMReaderCallback 无法通过委托桥接，
/// 改用 OnAudioFilterRead 在 Unity 音频线程中直接从 ManualSpeaker 拉取 PCM 数据。
/// </summary>
public class VCAndroidAudioPuller : MonoBehaviour
{
	private ManualSpeaker? _speaker;
	private ISampleProvider? _provider;
	private int _sampleRate;

    public void Init(ManualSpeaker speaker, int sampleRate)
    {
        if (speaker == null)
        {
            VoiceChatPluginMain.Logger.LogError("[VCAndroidAudioPuller] Init failed: speaker is null.");
            return;
        }
        _speaker = speaker;
        _provider = (ISampleProvider)speaker;
        _sampleRate = sampleRate;

        var src = GetComponent<AudioSource>();
        if (src == null)
        {
            VoiceChatPluginMain.Logger.LogError("[VCAndroidAudioPuller] AudioSource component missing.");
            return;
        }

        try
        {
            src.clip = AudioClip.Create("VC_Silence", _sampleRate, 2, _sampleRate, false);
            src.loop = true;
            src.volume = 0f;
            src.spatialBlend = 0f;
            src.Play();
        }
        catch (Exception ex)
        {
            VoiceChatPluginMain.Logger.LogError($"[VCAndroidAudioPuller] Failed to start silent clip: {ex.Message}");
        }
    }

    // Unity 在音频线程上调用此方法（非 IL2CPP 委托，可正常使用）
    private void OnAudioFilterRead(float[] data, int channels)
	{
		if (_provider == null) return;
		try
		{
			_provider.Read(data, 0, data.Length);
		}
		catch { }
	}
}
#endif