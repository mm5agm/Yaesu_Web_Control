using System;

namespace RadioWebControl.Core.Services.Rtty
{
    /// <summary>
    /// How much of one audio tone is present, moment by moment: mix the audio
    /// down against that tone, then average over a fixed window.
    ///
    /// <para>The average is a plain sliding sum rather than a recursive filter,
    /// because the thing being measured has hard edges in time. A window exactly
    /// one bit long is the matched filter for a FSK bit - the longest window that
    /// cannot straddle two bits, so it collects all the signal there is and the
    /// least noise possible - and it forgets a bit the instant the bit is over,
    /// where a recursive filter would carry a little of the previous bit into the
    /// next one for ever.</para>
    ///
    /// <para>Shared by the demodulator, which runs two of these at one bit each
    /// and subtracts them, and by the analyser, which runs them to measure how
    /// often each tone is winning before it knows what the speed is.</para>
    /// </summary>
    public sealed class RttyToneMagnitude
    {
        private readonly double[] _i;
        private readonly double[] _q;
        private readonly double _step;
        private double _sumI, _sumQ, _phase;
        private int _at;

        /// <param name="window">Averaging length in samples. One bit time, where that is known.</param>
        /// <param name="hz">The tone to measure.</param>
        /// <param name="sampleRate">Hz.</param>
        public RttyToneMagnitude(int window, double hz, int sampleRate)
        {
            if (window < 2) throw new ArgumentOutOfRangeException(nameof(window));
            if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));

            _i = new double[window];
            _q = new double[window];
            _step = 2 * Math.PI * hz / sampleRate;
        }

        public void Reset()
        {
            Array.Clear(_i);
            Array.Clear(_q);
            _sumI = _sumQ = _phase = 0;
            _at = 0;
        }

        /// <summary>One sample in, the tone's current magnitude out.</summary>
        public double Process(float sample)
        {
            var i = sample * Math.Cos(_phase);
            var q = -sample * Math.Sin(_phase);

            _phase += _step;
            if (_phase > 2 * Math.PI) _phase -= 2 * Math.PI;

            _sumI += i - _i[_at];
            _sumQ += q - _q[_at];
            _i[_at] = i;
            _q[_at] = q;
            if (++_at == _i.Length) _at = 0;

            return Math.Sqrt(_sumI * _sumI + _sumQ * _sumQ) / _i.Length;
        }
    }
}
