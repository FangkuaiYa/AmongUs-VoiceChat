using System;
using System.Collections.Generic;
using System.IO;
using NAudio.Dsp;
using NAudio.Wave;

namespace VoiceChatPlugin.Audio;

// ===========================================================================
// Abstract base
// ===========================================================================

public abstract class AbstractAudioRouter
{
    internal int  Id               { get; set; } = -1;
    internal bool HasMultipleInput { get; set; }
    internal bool HasMultipleOutput => _children.Count > 1;
    public   bool IsGlobalRouter   { get; set; }
    internal int  Channels                   = 1;
    virtual  internal int OutputChannels     => Channels;

    protected abstract internal bool ShouldBeGivenStereoInput { get; }
    protected abstract internal bool IsEndpoint               { get; }
    internal  abstract ISampleProvider GenerateProcessor(ISampleProvider source);

    private readonly List<AbstractAudioRouter> _children = new();
    internal IEnumerable<AbstractAudioRouter> GetChildRouters() => _children;

    public void Connect(AbstractAudioRouter child)
    {
        if (child.Id != -1 || Id != -1)
            throw new InvalidOperationException("Cannot use a finalized router.");
        _children.Add(child);
    }
}

public interface IHasAudioPropertyNode
{
    internal AudioRoutingInstanceNode GetProperty(int id);
}

public abstract class AbstractAudioNodeProvider<TProperty> : AbstractAudioRouter
    where TProperty : class, ISampleProvider
{
    public TProperty GetProperty(IHasAudioPropertyNode holder)
        => (holder.GetProperty(Id).Processor as TProperty)!;
}

// ===========================================================================
// AudioManager  (replaces the internal Interstellar.Routing.AudioManager)
// ===========================================================================

public class AudioManager : IHasAudioPropertyNode
{
    private readonly AbstractAudioRouter        _router;
    private readonly int                        _bufLen, _bufMax;
    private readonly AudioRoutingInstanceNode[] _globals;
    private readonly int                        _nodeCount;
    private          ISampleProvider?           _endpoint;
    private readonly List<AudioBuffer>          _buffers = new();

    public ISampleProvider? Endpoint => _endpoint;

    // Wrap endpoint so AudioBuffers are cleared each pull cycle
    private class EndpointWrapper : ISampleProvider
    {
        private readonly ISampleProvider _inner;
        private readonly List<AudioBuffer> _bufs;
        public WaveFormat WaveFormat => _inner.WaveFormat;
        public EndpointWrapper(ISampleProvider inner, List<AudioBuffer> bufs)
        { _inner = inner; _bufs = bufs; }
        public int Read(float[] buffer, int offset, int count)
        { foreach (var b in _bufs) b.Clear(); return _inner.Read(buffer, offset, count); }
    }

    public AudioManager(AbstractAudioRouter router, int bufLen = 2048, int bufMax = 4096)
    {
        _router  = router;
        _bufLen  = bufLen;
        _bufMax  = bufMax;
        _nodeCount = AssignIds(router);
        _globals   = BuildGlobalNodes();
    }

    // ── ID assignment ──────────────────────────────────────────────────────────
    private static int AssignIds(AbstractAudioRouter root)
    {
        int next = 0;
        void SetId(AbstractAudioRouter r, bool stereoParent, bool inGlobal)
        {
            if (r.Id == -1)
            {
                r.Id = next++;
                if (r.IsGlobalRouter && !inGlobal) r.HasMultipleInput = true;
                if (stereoParent || r.ShouldBeGivenStereoInput) r.Channels = 2;
                bool childStereo = stereoParent || r.OutputChannels == 2;
                foreach (var c in r.GetChildRouters())
                    SetId(c, childStereo, r.IsGlobalRouter || inGlobal);
            }
            else
            {
                r.HasMultipleInput = true;
                if (r.Channels == 1 && stereoParent) SetAllStereo(r);
            }
        }
        SetId(root, false, false);
        return next;
    }

    private static void SetAllStereo(AbstractAudioRouter r)
    {
        r.Channels = 2;
        foreach (var c in r.GetChildRouters()) SetAllStereo(c);
    }

    // ── Global nodes (shared across all AudioRoutingInstances) ─────────────────
    private AudioRoutingInstanceNode[] BuildGlobalNodes()
    {
        var nodes = new AudioRoutingInstanceNode[_nodeCount];
        void Build(AbstractAudioRouter r, ISampleProvider? parent, bool inGlobal)
        {
            if (r.IsGlobalRouter)
            {
                if (nodes[r.Id] == null)
                {
                    nodes[r.Id] = new AudioRoutingInstanceNode(
                        _buffers, parent!, r.GenerateProcessor,
                        r.HasMultipleInput, r.HasMultipleOutput, r.Channels, -1);
                    if (r.IsEndpoint)
                        _endpoint = new EndpointWrapper(nodes[r.Id].Processor, _buffers);
                }
                else if (parent != null)
                {
                    nodes[r.Id].AddInput(parent, -1);
                }
            }
            else if (inGlobal)
            {
                throw new InvalidDataException("Non-global router cannot be a child of a global router.");
            }
            foreach (var c in r.GetChildRouters())
                Build(c, nodes[r.Id]?.Processor, r.IsGlobalRouter || inGlobal);
        }
        Build(_router, null, false);
        return nodes;
    }

    // ── Generate a per-client AudioRoutingInstance ─────────────────────────────
    public AudioRoutingInstance Generate(int groupId)
    {
        var nodes = new AudioRoutingInstanceNode[_nodeCount];
        Array.Copy(_globals, nodes, _nodeCount);

        var source = new BufferedSampleProvider(
            WaveFormat.CreateIeeeFloatWaveFormat(AudioHelpers.ClockRate, 1),
            _bufMax)
        {
            DiscardOnBufferOverflow = true,
            BufferCutSize  = _bufMax,
            BufferCutToSize = _bufLen,
        };

        void Build(AbstractAudioRouter r, ISampleProvider? parent)
        {
            if (nodes[r.Id] == null)
            {
                nodes[r.Id] = new AudioRoutingInstanceNode(
                    _buffers, parent!, r.GenerateProcessor,
                    r.HasMultipleInput, r.HasMultipleOutput, r.Channels, groupId);
            }
            else if (parent != null)
            {
                nodes[r.Id].AddInput(parent, groupId);
            }
            if (!r.IsGlobalRouter)
                foreach (var c in r.GetChildRouters()) Build(c, nodes[r.Id]?.Processor);
        }
        Build(_router, source);
        return new AudioRoutingInstance(nodes, source);
    }

    // ── Remove a client's contribution from all global mixers ─────────────────
    public void Remove(int groupId)
    {
        foreach (var n in _globals) n?.RemoveInput(groupId);
        _buffers.RemoveAll(b => b.GroupId == groupId);
    }

    AudioRoutingInstanceNode IHasAudioPropertyNode.GetProperty(int id) => _globals[id];
}

// ===========================================================================
// Router implementations
// ===========================================================================

public class SimpleRouter : AbstractAudioRouter
{
    protected internal override bool ShouldBeGivenStereoInput => false;
    protected internal override bool IsEndpoint               => false;
    internal override ISampleProvider GenerateProcessor(ISampleProvider src) => src;
    public SimpleRouter(bool isGlobal = false) { IsGlobalRouter = isGlobal; }
}

public class SimpleEndpoint : AbstractAudioRouter
{
    public SimpleEndpoint() { IsGlobalRouter = true; }
    protected internal override bool ShouldBeGivenStereoInput => false;
    protected internal override bool IsEndpoint               => true;
    internal override ISampleProvider GenerateProcessor(ISampleProvider src) => src;
}

public class VolumeRouter : AbstractAudioNodeProvider<VolumeRouter.Property>
{
    public class Property : ISampleProvider
    {
        private readonly ISampleProvider _src;
        public float Volume { get; set; }
        public WaveFormat WaveFormat => _src.WaveFormat;
        internal Property(ISampleProvider src) => _src = src;

        public int Read(float[] buffer, int offset, int count)
        {
            if (Volume > 0f)
            {
                int r = _src.Read(buffer, offset, count);
                if (Volume != 1f) for (int i = 0; i < count; i++) buffer[offset + i] *= Volume;
                return r;
            }
            Array.Clear(buffer, offset, count);
            return count;
        }
    }
    protected internal override bool ShouldBeGivenStereoInput => false;
    protected internal override bool IsEndpoint               => false;
    internal override ISampleProvider GenerateProcessor(ISampleProvider src) => new Property(src);
}

public class StereoRouter : AbstractAudioNodeProvider<StereoRouter.Property>
{
    public class Property : ISampleProvider
    {
        private readonly StereoSampleProvider _sp;
        public float Volume { get => _sp.Volume; set => _sp.Volume = value; }
        public float Pan    { get => _sp.Pan;    set => _sp.Pan    = value; }
        public WaveFormat WaveFormat => _sp.WaveFormat;
        public int Read(float[] buf, int off, int cnt) => _sp.Read(buf, off, cnt);
        internal Property(ISampleProvider src) => _sp = new StereoSampleProvider(src);
    }
    protected internal override bool ShouldBeGivenStereoInput => false;
    protected internal override bool IsEndpoint               => false;
    internal override int OutputChannels => 2;
    internal override ISampleProvider GenerateProcessor(ISampleProvider src)
    {
        if (src.WaveFormat.Channels == 2)
            throw new InvalidDataException("StereoRouter requires mono input.");
        return new Property(src);
    }
}

public class LevelMeterRouter : AbstractAudioNodeProvider<LevelMeterRouter.Property>
{
    public class Property : ISampleProvider
    {
        private readonly ISampleProvider _src;
        public float Decay { get; set; } = 0.5f;
        public float Level { get; private set; }
        public WaveFormat WaveFormat => _src.WaveFormat;
        internal Property(ISampleProvider src) => _src = src;

        public int Read(float[] buffer, int offset, int count)
        {
            int r = _src.Read(buffer, offset, count);
            Level -= Decay * ((float)count / AudioHelpers.ClockRate);
            if (Level < 0f) Level = 0f;
            for (int i = 0; i < r; i++) if (Level < buffer[offset + i]) Level = buffer[offset + i];
            if (Level > 1f) Level = 1f;
            return r;
        }
    }
    protected internal override bool ShouldBeGivenStereoInput => false;
    protected internal override bool IsEndpoint               => false;
    internal override ISampleProvider GenerateProcessor(ISampleProvider src) => new Property(src);
}

public class FilterRouter : AbstractAudioRouter
{
    private readonly Func<BiQuadFilter> _gen;
    protected internal override bool ShouldBeGivenStereoInput => false;
    protected internal override bool IsEndpoint               => false;

    private FilterRouter(Func<BiQuadFilter> gen, bool global = false)
    { _gen = gen; IsGlobalRouter = global; }

    public static FilterRouter CreateLowPassFilter (float freq, float q, bool global = false)
        => new(() => BiQuadFilter.LowPassFilter(AudioHelpers.ClockRate, freq, q), global);
    public static FilterRouter CreateHighPassFilter(float freq, float q, bool global = false)
        => new(() => BiQuadFilter.HighPassFilter(AudioHelpers.ClockRate, freq, q), global);
    public static FilterRouter CreateBandPassFilter(float freq, float q, bool global = false)
        => new(() => BiQuadFilter.BandPassFilterConstantPeakGain(AudioHelpers.ClockRate, freq, q), global);
    public static FilterRouter CreateNotchFilter   (float freq, float q, bool global = false)
        => new(() => BiQuadFilter.NotchFilter(AudioHelpers.ClockRate, freq, q), global);

    internal override ISampleProvider GenerateProcessor(ISampleProvider src)
        => src.WaveFormat.Channels == 2
            ? new FilteredStereoProvider(src, _gen(), _gen())
            : new FilteredMonoProvider(src, _gen());

    internal class FilteredMonoProvider : ISampleProvider
    {
        private readonly ISampleProvider _src;
        private readonly BiQuadFilter    _f;
        public WaveFormat WaveFormat => _src.WaveFormat;
        internal FilteredMonoProvider(ISampleProvider src, BiQuadFilter f) { _src = src; _f = f; }
        public int Read(float[] buf, int off, int cnt)
        {
            int r = _src.Read(buf, off, cnt);
            for (int i = 0; i < r; i++) buf[off + i] = _f.Transform(buf[off + i]);
            return r;
        }
    }

    internal class FilteredStereoProvider : ISampleProvider
    {
        private readonly ISampleProvider _src;
        private readonly BiQuadFilter    _fL, _fR;
        public WaveFormat WaveFormat => _src.WaveFormat;
        internal FilteredStereoProvider(ISampleProvider src, BiQuadFilter fL, BiQuadFilter fR)
        { _src = src; _fL = fL; _fR = fR; }
        public int Read(float[] buf, int off, int cnt)
        {
            int r = _src.Read(buf, off, cnt);
            for (int i = 0; i < r; i++)
                buf[off + i] = (i % 2 == 0) ? _fL.Transform(buf[off + i]) : _fR.Transform(buf[off + i]);
            return r;
        }
    }
}

public class ReverbRouter : AbstractAudioNodeProvider<ReverbRouter.Property>
{
    public class Property : ISampleProvider
    {
        private readonly ReverbSampleProvider _sp;
        public float Decay     { get => _sp.Decay;     set => _sp.Decay     = value; }
        public float WetDryMix { get => _sp.WetDryMix; set => _sp.WetDryMix = value; }
        public WaveFormat WaveFormat => _sp.WaveFormat;
        public int Read(float[] buf, int off, int cnt) => _sp.Read(buf, off, cnt);
        internal Property(ISampleProvider src, int ms, float decay, float wet)
            => _sp = new ReverbSampleProvider(src, ms, decay, wet);
    }
    protected internal override bool ShouldBeGivenStereoInput => false;
    protected internal override bool IsEndpoint               => false;
    private readonly int   _ms;
    private readonly float _decay, _wet;
    public ReverbRouter(int delayMs, float decay = 0.3f, float wetDry = 0.5f)
    { _ms = delayMs; _decay = decay; _wet = wetDry; }
    internal override ISampleProvider GenerateProcessor(ISampleProvider src)
        => new Property(src, _ms, _decay, _wet);
}

public class DistortionFilter : AbstractAudioNodeProvider<DistortionFilter.Property>
{
    public class Property : ISampleProvider
    {
        private readonly ISampleProvider _src;
        public float Threshold    { get; set; }
        public bool  Amplification{ get; set; }
        public WaveFormat WaveFormat => _src.WaveFormat;
        internal Property(ISampleProvider src) => _src = src;
        public int Read(float[] buf, int off, int cnt)
        {
            int r = _src.Read(buf, off, cnt);
            if (Amplification)
            {
                float amp = 1f / Threshold;
                for (int i = 0; i < r; i++)
                {
                    float s = buf[off + i];
                    buf[off + i] = s > Threshold ? 1f : s < -Threshold ? -1f : s * amp;
                }
            }
            else
            {
                for (int i = 0; i < r; i++)
                {
                    float s = buf[off + i];
                    buf[off + i] = s > Threshold ? Threshold : s < -Threshold ? -Threshold : s;
                }
            }
            return r;
        }
    }
    public float DefaultThreshold    { get; set; } = 1f;
    public bool  DefaultAmplification{ get; set; }
    protected internal override bool ShouldBeGivenStereoInput => false;
    protected internal override bool IsEndpoint               => false;
    internal override ISampleProvider GenerateProcessor(ISampleProvider src)
        => new Property(src) { Threshold = DefaultThreshold, Amplification = DefaultAmplification };
}
