using System;
using Interstellar.Voice;
using UnityEngine;

namespace Interstellar.Android;

public class AndroidMicrophone : IDisposable
{
    private const int TargetSampleRate = 48000;
    private const int TargetFrameSamples = 1920;
    private const int PushChunkSize = 480;
    private const int MaxReadFrames = 12;
    private const int MaxScratchSamples = TargetFrameSamples * MaxReadFrames;
    private const float PermissionRetryInterval = 1.0f;
    private const float StartRetryBaseInterval = 0.5f;
    private const float StartRetryMaxInterval = 10f;

    private readonly ManualMicrophone _manualMic;
    private readonly string _logTag = "[VC:AndroidMic]";

    private bool _running;
    private bool _permissionRequested;
    private int _sourceSampleRate = TargetSampleRate;
    private int _sourceFrameSamples = TargetFrameSamples;

    private float[] _readScratch = new float[MaxScratchSamples];
    private float[] _sourceFrame = new float[TargetFrameSamples];
    private float[] _voiceFrame = new float[TargetFrameSamples];
    private float[] _pushBuffer = new float[PushChunkSize];
    private float[] _sourceAccum = new float[TargetFrameSamples * 2];
    private int _sourceAccumCount;

    private int _totalFramesCaptured;
    private int _lastAvailableSamples;
    private string _lastStatus = "not started";

    private float _nextPermissionCheckTime;
    private float _nextStartRetryTime;
    private int _startRetryCount;

    private bool _loggedStartSuccess;
    private bool _loggedFirstSamples;
    private bool _loggedPermRequest;

    public bool IsRunning => _running && StarlightVoiceNative.IsCaptureRunning();
    public int TotalFramesCaptured => _totalFramesCaptured;
    public int LastAvailableSamples => _lastAvailableSamples;
    public string LastStatus => _lastStatus;
    public int SourceSampleRate => _sourceSampleRate;
    public ManualMicrophone Microphone => _manualMic;

    public AndroidMicrophone()
    {
        _manualMic = new ManualMicrophone();
    }

    /// <summary>Call early to kick off permission request and AudioRecord startup
    /// before the WebSocket connection completes.</summary>
    public void Warmup()
    {
        EnsureStarted();
    }

    public void Update()
    {
        if (!EnsureStarted())
            return;

        int read = StarlightVoiceNative.ReadFloat(_readScratch, _readScratch.Length);

        if (read < 0)
        {
            _lastStatus = $"read error: {read} / {StarlightVoiceNative.GetLastError()}";
            InterstellarPlugin.Logger.LogWarning(
                $"{_logTag} ReadFloat returned {read}: {StarlightVoiceNative.GetLastError()}");
            return;
        }

        _lastAvailableSamples = read;

        if (read == 0)
        {
            _lastStatus = "waiting for samples";
            return;
        }

        _lastStatus = "capturing";

        if (_sourceSampleRate == TargetSampleRate)
            PushDirectly(read);
        else
            PushResampled(read);

        if (!_loggedFirstSamples)
        {
            InterstellarPlugin.Logger.LogInfo(
                $"{_logTag} First frame captured at {_sourceSampleRate} Hz.");
            _loggedFirstSamples = true;
        }
    }

    private void PushDirectly(int totalRead)
    {
        int offset = 0;
        while (offset < totalRead)
        {
            int chunk = Math.Min(PushChunkSize, totalRead - offset);
            Array.Copy(_readScratch, offset, _pushBuffer, 0, chunk);

            if (chunk < PushChunkSize)
            {
                float[] smallChunk = new float[chunk];
                Array.Copy(_pushBuffer, 0, smallChunk, 0, chunk);
                _manualMic.PushAudioData(smallChunk);
            }
            else
            {
                _manualMic.PushAudioData(_pushBuffer);
            }

            offset += chunk;
            _totalFramesCaptured++;
        }
    }

    private void PushResampled(int totalRead)
    {
        int space = _sourceAccum.Length - _sourceAccumCount;
        int toCopy = Math.Min(totalRead, space);
        Array.Copy(_readScratch, 0, _sourceAccum, _sourceAccumCount, toCopy);
        _sourceAccumCount += toCopy;

        if (toCopy < totalRead)
        {
            int overflow = totalRead - toCopy;
            InterstellarPlugin.Logger.LogWarning(
                $"{_logTag} Accumulator overflow, dropping {overflow} samples.");
            Array.Copy(_sourceAccum, overflow, _sourceAccum, 0, _sourceAccumCount - overflow);
            _sourceAccumCount -= overflow;
            Array.Copy(_readScratch, toCopy, _sourceAccum, _sourceAccumCount, overflow);
            _sourceAccumCount += overflow;
        }

        while (_sourceAccumCount >= _sourceFrameSamples)
        {
            Array.Copy(_sourceAccum, 0, _sourceFrame, 0, _sourceFrameSamples);
            ResampleTo48kHz(_sourceFrame, _sourceFrameSamples);
            _manualMic.PushAudioData(_voiceFrame);
            _totalFramesCaptured++;

            int remaining = _sourceAccumCount - _sourceFrameSamples;
            if (remaining > 0)
                Array.Copy(_sourceAccum, _sourceFrameSamples, _sourceAccum, 0, remaining);
            _sourceAccumCount = remaining;
        }
    }

    public void Dispose()
    {
        if (!_running) return;

        StarlightVoiceNative.StopCapture();
        _running = false;
        _loggedStartSuccess = false;
        _loggedFirstSamples = false;
        _startRetryCount = 0;
        _sourceAccumCount = 0;
        _lastStatus = "stopped";
        InterstellarPlugin.Logger.LogInfo($"{_logTag} Capture stopped.");
    }

    private bool EnsureStarted()
    {
        if (_running && StarlightVoiceNative.IsCaptureRunning())
            return true;

        if (!StarlightVoiceNative.HasRecordAudioPermission())
        {
            if (!_permissionRequested)
            {
                _permissionRequested = true;
                InterstellarPlugin.Logger.LogInfo($"{_logTag} Requesting mic permission.");
            }

            float now = Time.unscaledTime;
            if (now < _nextPermissionCheckTime)
            {
                _lastStatus = "waiting for microphone permission";
                return false;
            }

            StarlightVoiceNative.RequestRecordAudioPermission();
            _nextPermissionCheckTime = now + PermissionRetryInterval;
            _lastStatus = "requesting microphone permission";

            if (!_loggedPermRequest)
            {
                InterstellarPlugin.Logger.LogInfo($"{_logTag} Permission request sent.");
                _loggedPermRequest = true;
            }
            return false;
        }

        float now2 = Time.unscaledTime;
        if (now2 < _nextStartRetryTime)
        {
            _lastStatus = $"retrying start in {(_nextStartRetryTime - now2):F1}s";
            return false;
        }

        int result = StarlightVoiceNative.StartCapture(TargetSampleRate, TargetFrameSamples);

        if (result <= 0)
        {
            _lastStatus = $"start failed: {result}, {StarlightVoiceNative.GetLastError()}";
            InterstellarPlugin.Logger.LogWarning(
                $"{_logTag} StartCapture returned {result}: {StarlightVoiceNative.GetLastError()}");

            _startRetryCount++;
            float delay = Math.Min(
                StartRetryBaseInterval * Mathf.Pow(2f, _startRetryCount - 1), StartRetryMaxInterval);
            _nextStartRetryTime = now2 + delay;
            return false;
        }

        _sourceSampleRate = result;
        _sourceFrameSamples = Math.Max(1, result * 40 / 1000);
        EnsureBuffers();
        _sourceAccumCount = 0;
        _running = true;
        _startRetryCount = 0;
        _permissionRequested = true;
        _lastStatus = $"started at {_sourceSampleRate} Hz";

        if (!_loggedStartSuccess)
        {
            InterstellarPlugin.Logger.LogInfo(
                $"{_logTag} AudioRecord started: {_sourceSampleRate} Hz, " +
                $"buffer={StarlightVoiceNative.GetBufferFrames()} frames.");
            _loggedStartSuccess = true;
        }

        return true;
    }

    private void EnsureBuffers()
    {
        int readCapacity = Math.Max(_sourceFrameSamples * MaxReadFrames, PushChunkSize * 4);
        if (_readScratch.Length < readCapacity)
            _readScratch = new float[readCapacity];

        if (_sourceFrame.Length < _sourceFrameSamples)
            _sourceFrame = new float[_sourceFrameSamples];

        int minAccum = _sourceFrameSamples * 2;
        if (_sourceAccum.Length < minAccum)
        {
            var newAccum = new float[minAccum];
            Array.Copy(_sourceAccum, 0, newAccum, 0, Math.Min(_sourceAccumCount, minAccum));
            _sourceAccum = newAccum;
        }
    }

    private void ResampleTo48kHz(float[] source, int sourceFrameSamples)
    {
        if (sourceFrameSamples == TargetFrameSamples)
        {
            Array.Copy(source, _voiceFrame, TargetFrameSamples);
            return;
        }

        if (sourceFrameSamples <= 1)
        {
            Array.Clear(_voiceFrame, 0, _voiceFrame.Length);
            return;
        }

        float step = (float)(sourceFrameSamples - 1) / (float)(TargetFrameSamples - 1);
        for (int i = 0; i < TargetFrameSamples; i++)
        {
            float srcPos = (float)i * step;
            int left = (int)srcPos;
            int right = Math.Min(left + 1, sourceFrameSamples - 1);
            float frac = srcPos - (float)left;
            _voiceFrame[i] = source[left] + (source[right] - source[left]) * frac;
        }
    }
}
