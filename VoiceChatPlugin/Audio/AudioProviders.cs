using System;
using System.Collections.Generic;
using VoiceChatPlugin.NAudio.Dsp;
using VoiceChatPlugin.NAudio.Wave;

namespace VoiceChatPlugin.Audio;

// ---------------------------------------------------------------------------
// BufferedSampleProvider
// ---------------------------------------------------------------------------
internal class BufferedSampleProvider : ISampleProvider
{
    private CircularFloatBuffer? _ring;
    private readonly WaveFormat  _format;

    public bool ReadFully              { get; set; } = true;
    public int  BufferLength           { get; set; }
    public int  BufferCutSize          { get; set; } = int.MaxValue;
    public int  BufferCutToSize        { get; set; } = int.MaxValue;
    public bool DiscardOnBufferOverflow{ get; set; }

    public WaveFormat WaveFormat => _format;
    public int BufferedBytes => _ring?.Count ?? 0;

    public BufferedSampleProvider(WaveFormat waveFormat, int? bufferLength = null)
    {
        _format      = waveFormat;
        BufferLength = bufferLength ?? waveFormat.AverageBytesPerSecond * 5;
    }

    public void AddSamples(float[] buffer, int offset, int count)
    {
        _ring ??= new CircularFloatBuffer(BufferLength);
        int written = _ring.Write(buffer, offset, count);
        if (written < count && !DiscardOnBufferOverflow)
            throw new InvalidOperationException("Buffer full");
        if (_ring.Count > BufferCutSize && BufferCutSize > BufferCutToSize)
            _ring.Discard(_ring.Count - BufferCutToSize);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int num = _ring?.Read(buffer, offset, count) ?? 0;
        if (ReadFully && num < count)
        {
            Array.Clear(buffer, offset + num, count - num);
            num = count;
        }
        return num;
    }
}

// ---------------------------------------------------------------------------
// MonoToStereoSampleProvider
// ---------------------------------------------------------------------------
internal class MonoToStereoSampleProvider : ISampleProvider
{
    private static readonly WaveFormat _fmt =
        WaveFormat.CreateIeeeFloatWaveFormat(AudioHelpers.ClockRate, 2);
    private readonly ISampleProvider _src;
    public WaveFormat WaveFormat => _fmt;
    public MonoToStereoSampleProvider(ISampleProvider mono) => _src = mono;

    public int Read(float[] buffer, int offset, int count)
    {
        _src.Read(buffer, offset, count / 2);
        for (int i = count / 2 - 1; i >= 0; i--)
        {
            buffer[offset + i * 2]     = buffer[offset + i];
            buffer[offset + i * 2 + 1] = buffer[offset + i];
        }
        return count;
    }
}

// ---------------------------------------------------------------------------
// StereoSampleProvider  (panning + volume)
// ---------------------------------------------------------------------------
internal class StereoSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _src;
    private float[] _temp = null!;
    private float[] _last = null!;
    private int     _lastLen;
    private int     _lastLDelay, _lastRDelay;
    private float   _pan;
    private readonly object _panLock = new();

    public WaveFormat WaveFormat { get; }
    public float Volume { get; set; } = 1f;
    public float Pan
    {
        get { lock (_panLock) return _pan; }
        set { lock (_panLock) _pan = Math.Clamp(value, -1f, 1f); }
    }

    public StereoSampleProvider(ISampleProvider src)
    {
        _src       = src;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(src.WaveFormat.SampleRate, 2);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int mono = count / 2;
        if (_temp == null || _temp.Length < mono) _temp = new float[mono];
        int read = _src.Read(_temp, 0, mono);

        float pan    = Pan;
        float lCoeff = pan < 0 ? 0 : pan;
        float rCoeff = pan < 0 ? -pan : 0;
        int   lDelay = (int)(lCoeff * 50);
        int   rDelay = (int)(rCoeff * 50);
        float lVol   = (1f - lCoeff * 0.3f) * Volume;
        float rVol   = (1f - rCoeff * 0.3f) * Volume;
        int   lCount = mono - lDelay + _lastLDelay;
        int   rCount = mono - rDelay + _lastRDelay;

        for (int i = 0; i < mono; i++)
        {
            int lIdx = i * lCount / mono;
            buffer[offset + i * 2] = lIdx < _lastLDelay
                ? (_last != null ? _last[_lastLen - _lastLDelay + lIdx] * lVol : 0f)
                : _temp[lIdx - _lastLDelay] * lVol;

            int rIdx = i * rCount / mono;
            buffer[offset + i * 2 + 1] = rIdx < _lastRDelay
                ? (_last != null ? _last[_lastLen - _lastRDelay + rIdx] * rVol : 0f)
                : _temp[rIdx - _lastRDelay] * rVol;
        }

        _lastLDelay = lDelay;
        _lastRDelay = rDelay;
        (_last, _temp) = (_temp, _last!);
        _lastLen = read;
        return count;
    }
}

// ---------------------------------------------------------------------------
// ReverbSampleProvider
// ---------------------------------------------------------------------------
internal class ReverbSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _src;
    private readonly float[] _delay;
    private int   _pos;
    private float _decay, _wet, _dry;

    public float Decay      { get => _decay; set => _decay = Math.Clamp(value, 0f, 1f); }
    public float WetDryMix  { get => _wet;   set { _wet = Math.Clamp(value, 0f, 1f); _dry = 1f - _wet; } }
    public WaveFormat WaveFormat => _src.WaveFormat;

    public ReverbSampleProvider(ISampleProvider src, int delayMs, float decay, float wetDry)
    {
        _src   = src;
        int n  = (int)(src.WaveFormat.SampleRate * (delayMs / 1000f)) * src.WaveFormat.Channels;
        _delay = new float[n];
        Decay     = decay;
        WetDryMix = wetDry;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _src.Read(buffer, offset, count);
        for (int i = 0; i < read; i++)
        {
            float cur     = buffer[offset + i];
            float delayed = _delay[_pos];
            _delay[_pos]      = cur + delayed * _decay;
            buffer[offset + i] = cur * _dry + delayed * _wet;
            _pos = (_pos + 1) % _delay.Length;
        }
        return read;
    }
}

// ---------------------------------------------------------------------------
// AudioBuffer  (cache for nodes with multiple outputs)
// ---------------------------------------------------------------------------
internal class AudioBuffer : ISampleProvider
{
    private float[]?          _buf;
    private float[]?          _tmp;
    private int               _len;
    private readonly ISampleProvider _src;

    public int        GroupId    { get; }
    public WaveFormat WaveFormat => _src.WaveFormat;

    public AudioBuffer(ISampleProvider src, int groupId) { _src = src; GroupId = groupId; }

    public void Clear() => _buf = null;

    public int Read(float[] buffer, int offset, int count)
    {
        if (_buf == null)
        {
            if (_tmp != null && _tmp.Length >= count) _buf = _tmp;
            else _tmp = _buf = new float[count];
            int n = _src.Read(_buf, 0, count);
            if (n < count) Array.Clear(_buf, n, count - n);
            _len = count;
        }
        if (count != _len) throw new InvalidOperationException("Count must be consistent.");
        Buffer.BlockCopy(_buf, 0, buffer, offset * 4, count * 4);
        return count;
    }
}

// ---------------------------------------------------------------------------
// AudioMixer  (multiple-input node)
// ---------------------------------------------------------------------------
internal class AudioMixer : ISampleProvider
{
    private record struct Input(ISampleProvider Provider, int GroupId);
    private readonly List<Input> _inputs = new();
    private readonly WaveFormat  _fmt;
    private float[] _tmp = null!;

    public WaveFormat WaveFormat => _fmt;

    public AudioMixer(int channels)
        => _fmt = WaveFormat.CreateIeeeFloatWaveFormat(AudioHelpers.ClockRate, channels);

    public int Read(float[] buffer, int offset, int count)
    {
        if (_tmp == null || _tmp.Length < count) _tmp = new float[count];
        if (_inputs.Count == 0) { Array.Clear(buffer, offset, count); return count; }
        bool first = true;
        foreach (var inp in _inputs)
        {
            int r = inp.Provider.Read(_tmp, 0, count);
            if (first) { for (int i = 0; i < r; i++) buffer[offset + i]  = _tmp[i]; first = false; }
            else        { for (int i = 0; i < r; i++) buffer[offset + i] += _tmp[i]; }
        }
        return count;
    }

    public void AddInput(ISampleProvider src, int groupId)
    {
        if (src.WaveFormat.Channels == 1 && _fmt.Channels == 2)
            _inputs.Add(new(new MonoToStereoSampleProvider(src), groupId));
        else
            _inputs.Add(new(src, groupId));
    }

    public void RemoveInput(int groupId) => _inputs.RemoveAll(i => i.GroupId == groupId);
}

// ---------------------------------------------------------------------------
// AudioRoutingInstanceNode  (was internal in Interstellar)
// ---------------------------------------------------------------------------
internal class AudioRoutingInstanceNode
{
    private readonly AudioMixer?  _mixer;
    private readonly AudioBuffer? _buf;
    private readonly ISampleProvider _proc;

    public ISampleProvider Output    => _buf ?? _proc;
    public ISampleProvider Processor => _proc;

    public AudioRoutingInstanceNode(
        List<AudioBuffer>                  bufferList,
        ISampleProvider                    source,
        Func<ISampleProvider, ISampleProvider> ctor,
        bool hasMultipleInput,
        bool hasMultipleOutput,
        int  channels,
        int  groupId)
    {
        if (hasMultipleInput)
        {
            _mixer = new AudioMixer(channels);
            if (source != null) _mixer.AddInput(source, -1);
        }
        else
        {
            _mixer = null;
            if (source.WaveFormat.Channels == 1 && channels == 2)
                source = new MonoToStereoSampleProvider(source);
        }
        _proc = ctor(_mixer ?? source);
        if (hasMultipleOutput)
        {
            _buf = new AudioBuffer(_proc, groupId);
            bufferList.Add(_buf);
        }
    }

    public void AddInput(ISampleProvider src, int groupId) => _mixer?.AddInput(src, groupId);
    public void RemoveInput(int groupId)                   => _mixer?.RemoveInput(groupId);
}

// ---------------------------------------------------------------------------
// AudioRoutingInstance  (was public in Interstellar – keep public for VCPlayer)
// ---------------------------------------------------------------------------
public class AudioRoutingInstance : IHasAudioPropertyNode
{
    private readonly AudioRoutingInstanceNode[] _nodes;
    private readonly BufferedSampleProvider     _source;

    internal AudioRoutingInstance(
        AudioRoutingInstanceNode[] nodes,
        BufferedSampleProvider     source)
    {
        _nodes  = nodes;
        _source = source;
    }

    public void AddSamples(float[] samples, int offset, int count)
        => _source.AddSamples(samples, offset, count);

    // Explicit implementation so AbstractAudioNodeProvider<T>.GetProperty(instance) works.
    AudioRoutingInstanceNode IHasAudioPropertyNode.GetProperty(int id) => _nodes[id];
}
