using Interstellar.NAudio.Provider;
using NAudio.Dsp;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Interstellar.Routing.Router;

public class FilterRouter : AbstractAudioRouter
{
    public class FilteredMonoProvider : ISampleProvider
    {
        private BiQuadFilter filter;
        private ISampleProvider source;
        WaveFormat ISampleProvider.WaveFormat => source.WaveFormat;

        int ISampleProvider.Read(float[] buffer, int offset, int count)
        {
            int read = source.Read(buffer, offset, count);
            for (int n = 0; n < read; n++)
            {
                buffer[offset + n] = filter.Transform(buffer[offset + n]);
            }
            return read;
        }

        internal FilteredMonoProvider(ISampleProvider source, BiQuadFilter filter)
        {
            this.source = source;
            this.filter = filter;
        }
    }

    public class FilteredStereoProvider : ISampleProvider
    {
        private BiQuadFilter filterL, filterR;
        private ISampleProvider source;
        WaveFormat ISampleProvider.WaveFormat => source.WaveFormat;

        int ISampleProvider.Read(float[] buffer, int offset, int count)
        {
            int read = source.Read(buffer, offset, count);
            for (int n = 0; n < read; n++)
            {
                if(n % 2 == 0)
                    buffer[offset + n] = filterL.Transform(buffer[offset + n]);
                else
                    buffer[offset + n] = filterR.Transform(buffer[offset + n]);
            }
            return read;
        }

        internal FilteredStereoProvider(ISampleProvider source, BiQuadFilter filterL, BiQuadFilter filterR)
        {
            this.source = source;
            this.filterL = filterL;
            this.filterR = filterR;
        }
    }
    protected internal override bool ShouldBeGivenStereoInput => false;
    protected internal override bool IsEndpoint => false;
    private Func<BiQuadFilter> filterGenerator;
    
    private FilterRouter(Func<BiQuadFilter> filterGenerator, bool isGlobalRouter = false)
    {
        this.filterGenerator = filterGenerator;
        IsGlobalRouter = isGlobalRouter;
    }

    /// <summary>
    /// Creates a low-pass filter that removes high frequencies and passes low frequencies.
    /// </summary>
    /// <param name="cutoffFrequency">Cutoff frequency in Hz.</param>
    /// <param name="qFactor">Q factor (resonance).</param>
    /// <param name="isGlobalRouter">Whether this router is global (shared across clients).</param>
    /// <returns>A new FilterRouter instance.</returns>
    static public FilterRouter CreateLowPassFilter(float cutoffFrequency, float qFactor, bool isGlobalRouter = false)
    {
        return new FilterRouter(() => BiQuadFilter.LowPassFilter(AudioConstants.ClockRate, cutoffFrequency, qFactor), isGlobalRouter);
    }

    /// <summary>
    /// Creates a high-pass filter that removes low frequencies and passes high frequencies.
    /// </summary>
    /// <param name="cutoffFrequency">Cutoff frequency in Hz.</param>
    /// <param name="qFactor">Q factor (resonance).</param>
    /// <param name="isGlobalRouter">Whether this router is global (shared across clients).</param>
    /// <returns>A new FilterRouter instance.</returns>
    static public FilterRouter CreateHighPassFilter(float cutoffFrequency, float qFactor, bool isGlobalRouter = false)
    {
        return new FilterRouter(() => BiQuadFilter.HighPassFilter(AudioConstants.ClockRate, cutoffFrequency, qFactor), isGlobalRouter);
    }

    /// <summary>
    /// Creates a band-pass filter centered at the given frequency.
    /// </summary>
    /// <param name="centerFrequency">Center frequency in Hz.</param>
    /// <param name="qFactor">Q factor (resonance).</param>
    /// <param name="isGlobalRouter">Whether this router is global (shared across clients).</param>
    /// <returns>A new FilterRouter instance.</returns>
    static public FilterRouter CreateBandPassFilter(float centerFrequency, float qFactor, bool isGlobalRouter = false)
    {
        return new FilterRouter(() => BiQuadFilter.BandPassFilterConstantPeakGain(AudioConstants.ClockRate, centerFrequency, qFactor), isGlobalRouter);
    }

    /// <summary>
    /// Creates a notch filter that removes a specific frequency band.
    /// </summary>
    /// <param name="centerFrequency">Center frequency in Hz.</param>
    /// <param name="qFactor">Q factor (resonance).</param>
    /// <param name="isGlobalRouter">Whether this router is global (shared across clients).</param>
    /// <returns>A new FilterRouter instance.</returns>
    static public FilterRouter CreateNotchFilter(float centerFrequency, float qFactor, bool isGlobalRouter = false)
    {
        return new FilterRouter(() => BiQuadFilter.NotchFilter(AudioConstants.ClockRate, centerFrequency, qFactor), isGlobalRouter);
    }



    internal override ISampleProvider GenerateProcessor(ISampleProvider source)
    {
        if (source.WaveFormat.Channels == 2)
        {
            return new FilteredStereoProvider(source, filterGenerator(), filterGenerator());
        }
        else
        {
            return new FilteredMonoProvider(source, filterGenerator());
        }
    }

}
