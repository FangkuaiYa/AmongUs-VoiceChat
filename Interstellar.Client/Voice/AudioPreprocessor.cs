using System;

namespace Interstellar.Voice;

/// <summary>
/// Real-time microphone preprocessing applied before Opus encoding:
///   - DC offset removal
///   - 2nd-order high-pass Butterworth filter (cuts rumble / fan noise)
///   - adaptive noise gate (noise suppression)
///   - far-end echo suppression (squelches mic while remote audio is playing
///     and the local signal is below speech level, so speaker output is not
///     re-transmitted)
/// Runs on the microphone thread; all state is per-room and allocation-free.
/// </summary>
internal sealed class AudioPreprocessor
{
    private const float SampleRate = 48000f;
    private const float HighPassFreq = 100f; // Hz — below speech fundamental

    // High-pass biquad coefficients (2nd-order Butterworth, Q = 1/sqrt(2))
    private readonly float _b0, _b1, _b2, _a1, _a2;
    private float _x1, _x2, _y1, _y2; // filter state
    private float _dc;                // DC offset estimate
    private float _env;               // smoothed input envelope
    private float _envPeak;           // peak tracker for noise floor (holds peak longer)
    private float _noiseFloor;        // adaptive noise floor estimate
    private float _gain;              // smoothed applied gain (noise gate + echo duck)
    private float _agcGain = 1f;      // smoothed automatic-gain-control makeup gain
    private const float AgcTargetPeak = 0.35f;
    private const float AgcMinGain = 1f;
    private const float AgcMaxGain = 4f;
    private bool _inited;
    private float _vadHangover;       // seconds of VAD hangover remaining
    private const float VADHangoverSeconds = 0.35f;

    /// <summary>True while the current frame (or its recent tail) contains speech.</summary>
    public bool IsSpeech { get; private set; }

    public AudioPreprocessor()
    {
        float w = 2f * MathF.PI * HighPassFreq / SampleRate;
        float c = MathF.Cos(w);
        float alpha = MathF.Sin(w) / (2f * 0.70710678f); // Q = 1/sqrt(2)
        float a0 = 1f + alpha;
        float bH = (1f + c) * 0.5f;
        _b0 = bH / a0;
        _b1 = -(1f + c) / a0;
        _b2 = bH / a0;
        _a1 = (-2f * c) / a0;
        _a2 = (1f - alpha) / a0;
    }

    /// <param name="farEndLevel">Smoothed peak of incoming (remote) audio being played back, 0..1.</param>
    public void Process(float[] samples, int count, bool noiseSuppression, bool echoCancellation, float farEndLevel)
    {
        // 1) DC offset estimate from this frame's mean.
        float sum = 0f;
        for (int i = 0; i < count; i++) sum += samples[i];
        float mean = sum / count;
        _dc += (mean - _dc) * 0.01f;

        // 2) High-pass filter + envelope peak.
        float max = 0f;
        for (int i = 0; i < count; i++)
        {
            float x = samples[i] - _dc;
            float y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            _x2 = _x1; _x1 = x;
            _y2 = _y1; _y1 = y;

            float clamped = y < -1f ? -1f : (y > 1f ? 1f : y);
            samples[i] = clamped;
            float abs = clamped < 0f ? -clamped : clamped;
            if (abs > max) max = abs;
        }

        // Envelope smoothing: fast attack, slow release.
        float envK = max > _env ? 0.30f : 0.015f;
        _env += (max - _env) * envK;

        // Peak tracker for noise floor — holds the envelope peak longer so
        // the noise floor estimate stays close to the actual noise level.
        if (max > _envPeak) _envPeak = max;
        else _envPeak *= 0.998f; // slow decay (~1.5s half-life at 48 kHz / 960)

        // Noise floor: tracks the slow minimum of the peak envelope with
        // faster upward adaptation so it keeps up with changing noise.
        if (!_inited) { _noiseFloor = _envPeak; _inited = true; }
        if (_envPeak < _noiseFloor)
            _noiseFloor = _noiseFloor * 0.998f + _envPeak * 0.002f; // fast drop
        else
            _noiseFloor = _noiseFloor * 0.9995f + _envPeak * 0.0005f; // slow rise (~0.5s)

        // 5) Voice activity detection — adaptive threshold + 350ms hangover.
        float vadThresh = MathF.Max(_noiseFloor * 2.5f, 0.003f);
        float frameSeconds = count / SampleRate;
        if (_env > vadThresh) _vadHangover = VADHangoverSeconds;
        else _vadHangover -= frameSeconds;
        IsSpeech = _vadHangover > 0f;

        float target = 1f;

        if (noiseSuppression)
        {
            float speechThresh = MathF.Max(_noiseFloor * 3.5f, 0.005f);
            if (!IsSpeech)
            {
                float r = MathF.Min(_env / speechThresh, 1f);
                float r2 = r * r;
                target = 0.08f + 0.92f * r2;
            }
            else if (_env < speechThresh)
            {
                float r = _env / speechThresh;
                target = 0.60f + 0.40f * r;
            }
        }

        if (echoCancellation && farEndLevel > 0.01f)
        {
            float dominance = farEndLevel / (farEndLevel + _env + 1e-6f);
            float echoTarget = 1f - dominance * 0.92f;
            target = MathF.Min(target, echoTarget);
        }

        // Smooth gain to avoid clicks: close fast, open slower.
        float gk = target < _gain ? 0.4f : 0.15f;
        _gain += (target - _gain) * gk;

        if (IsSpeech && _env > 0.001f)
        {
            float desired = _env > 0f ? Math.Clamp(AgcTargetPeak / _env, AgcMinGain, AgcMaxGain) : _agcGain;
            float agcK = desired > _agcGain ? 0.03f : 0.15f;
            _agcGain += (desired - _agcGain) * agcK;
        }

        float totalGain = _gain * _agcGain;
        if (totalGain < 0.999f || totalGain > 1.001f)
        {
            for (int i = 0; i < count; i++)
            {
                float y = samples[i] * totalGain;
                samples[i] = y < -1f ? -1f : (y > 1f ? 1f : y);
            }
        }
    }
}
