using Interstellar.Audio.Provider;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Interstellar.Routing.Router;

/// <summary>
/// Router that converts mono audio to spatial stereo.
/// Pan and volume can be adjusted.
/// </summary>
public class StereoRouter : AbstractAudioNodeProvider<StereoRouter.Property>
{
    public class Property : ISampleProvider
    {
        private StereoSampleProvider sampleProvider;
        public float Volume { get => sampleProvider.Volume; set => sampleProvider.Volume = value; }
        public float Pan { get => sampleProvider.Pan; set => sampleProvider.Pan = value; }

        WaveFormat ISampleProvider.WaveFormat => sampleProvider.WaveFormat;

        int ISampleProvider.Read(float[] buffer, int offset, int count) => sampleProvider.Read(buffer, offset, count);

        internal Property(ISampleProvider source)
        {
            sampleProvider = new StereoSampleProvider(source);
        }
    }
    protected internal override bool ShouldBeGivenStereoInput => false;
    protected internal override bool IsEndpoint => false;
    override internal int OutputChannels => 2;
    internal override ISampleProvider GenerateProcessor(ISampleProvider source)
    {
        if (source.WaveFormat.Channels == 2) throw new InvalidDataException("StereoRouter can only be connected after the mono input.");
        return new Property(source);
    }


}