using System;
using System.Collections.Generic;
using System.Linq;
using RadioWebControl.Core.Services.Spectrum;

namespace RadioWebControl.Core.Services.Rtty
{
    /// <summary>What a stretch of audio looks like it is carrying.</summary>
    /// <param name="MarkHz">The tone the signal idles on.</param>
    /// <param name="SpaceHz">The other one. Below <paramref name="MarkHz"/> for a reversed signal.</param>
    /// <param name="ShiftHz">
    /// The measured separation, unrounded and not snapped to anything. Good to a
    /// few hertz - worst measured 10 Hz, on a 170 shift at 100 baud, where the two
    /// keying clusters nearly touch. <see cref="RttySignalAnalyser.SnapShift"/>
    /// turns it into one of the shifts a radio understands.
    /// </param>
    /// <param name="Baud">
    /// The measured speed, not rounded to anything. Good to a fraction of a baud,
    /// so a station on one of the odd press-circuit speeds - 56.9, 74.2 - is
    /// reported as it is rather than as the nearest famous number.
    /// <see cref="RttySignalAnalyser.SnapBaud"/> says whether it is a standard one.
    /// </param>
    /// <param name="Confidence">
    /// How sure the analyser is that this is a RTTY signal with these tones at this
    /// speed, 0 to 1. Below about 0.4 the answer is a guess and should be shown as
    /// one: an empty band scores about 0.02.
    ///
    /// <para><b>It says nothing about which way round the tones are.</b> That is
    /// <see cref="ToneMargin"/>, and keeping them apart is deliberate - a station
    /// sending a one-bit stop element is identified perfectly except for its
    /// polarity, and folding the one uncertainty into the other would either hide
    /// it or throw away a good answer to the other question. A caller showing this
    /// to an operator should read both.</para>
    /// </param>
    /// <param name="ToneMargin">
    /// How much better the tones this way round fitted than the other way round,
    /// from 0 to 1. Around 0.3 on a station using the usual one-and-a-half bit stop
    /// element; near zero on one using a one or two bit stop, where nothing in the
    /// keying distinguishes the two tones and the answer is a guess. Worth showing
    /// the operator as a reverse button to press rather than acting on silently.
    /// See <see cref="RttySignalAnalyser"/>.
    /// </param>
    /// <param name="BaudFit">
    /// How nearly every measured tone-reversal came out a whole number of bits
    /// long at the chosen speed, 0 to 1. The strongest part of the estimate.
    /// </param>
    public sealed record RttySignalEstimate(
        double MarkHz,
        double SpaceHz,
        double ShiftHz,
        double Baud,
        double Confidence,
        double ToneMargin,
        double BaudFit);

    /// <summary>
    /// Listens to a few seconds of audio and works out what the signal in it is:
    /// the two tones, which way round they are, and the speed.
    ///
    /// <para>The point is that a listener tuning across a band has none of that
    /// information. On the amateur bands it is 2125/2295 at 45.45 baud and
    /// always has been, but a short-wave listener meets 425 and 450 Hz shifts on
    /// the weather and press stations, 850 Hz on military circuits, and 50, 75
    /// and 100 baud alongside 45.45. Guessing by hand means six dropdown
    /// permutations and a judgement about reverse.</para>
    ///
    /// <para><b>What it needs.</b> Both tones have to be inside the receiver's
    /// audio passband and visible above the noise - roughly, if they can be seen
    /// as two lines in a waterfall, this will find them. It needs a few seconds,
    /// because the speed is measured from the lengths of the keying intervals and
    /// a handful of them is not enough. It does not need the operator to be
    /// anywhere near correctly tuned, since it searches the whole passband; being
    /// off tune only matters once a tone falls off the edge of it.</para>
    ///
    /// <para><b>Two measurements.</b> The tones come from the spectrum. Which of
    /// them is mark, and how fast the station is sending, come out together from
    /// the lengths of the keying runs - they are one question, not two, and
    /// answering them separately is what the first version of this got wrong.</para>
    ///
    /// <para><b>The tones</b> are the two strongest peaks in an averaged spectrum,
    /// far enough apart to be a shift rather than one peak measured twice. Solid,
    /// as long as nothing louder than the wanted signal shares the passband -
    /// another station or a carrier will be picked in preference, and there is no
    /// way for this to know. See <see cref="HighestPeak"/> for how each one is
    /// pinned down to a hertz or two.</para>
    ///
    /// <para><b>The speed and the polarity</b> both come from one fact: a keying
    /// run is a whole number of bits long - <em>except</em> that the stop element
    /// is one and a <em>half</em> bits, and the stop element is always mark. So the
    /// space runs are whole bits and the mark runs are not, and that single
    /// asymmetry answers both questions. Measure every run; then sweep the speed
    /// across the whole range a quarter of a percent at a time, and at each step,
    /// for each of the two ways round the tones could be, ask how nearly the runs of
    /// the <em>assumed space tone</em> come out a whole number of bits. The peak of
    /// that sweep is the answer to both. A 6% speed error leaves visible remainders
    /// on the longer runs, which is exactly the error that framing checks are blind
    /// to; and getting the tones the wrong way round puts a stop element into every
    /// run being measured, which wrecks the fit outright.</para>
    ///
    /// <para><b>It sweeps rather than trying a list of famous speeds</b> so that it
    /// can report a station on a speed nobody tabulated - 56.9 and 74.2 turn up on
    /// press circuits, and some stations are simply off frequency-standard. The
    /// operator is told what was measured, and
    /// <see cref="SnapBaud"/> says whether that happens to be a speed with a name.
    /// Which peak of the sweep to take is not "the best one" - see
    /// <see cref="PickSpeed"/>, where the only real trap lives.</para>
    ///
    /// <para><b>The one station this cannot place</b> is the one sending a one-bit
    /// stop element, because then every run really is a whole number of bits and
    /// there is nothing in the keying to tell the two tones apart. Its tones and
    /// its speed come out exactly right and its polarity is a coin toss - so what
    /// matters is that it says so, and it does:
    /// <see cref="RttySignalEstimate.ToneMargin"/> collapses to near zero while
    /// <see cref="RttySignalEstimate.Confidence"/> stays high, which is the honest
    /// description of what is and is not known.</para>
    ///
    /// <para><b>Why not simply ask which tone is sounding for longer.</b> Because
    /// it does not work, which took a measurement to establish. RTTY idles on mark
    /// and the stop element is mark, so mark ought to lead - but the common English
    /// letters have mark-sparse ITA-2 codes (E is one mark bit in five, T likewise)
    /// and the data bits cancel the stop element advantage almost exactly. Over a
    /// page of plain English text mark won 50.5% of the time: a coin toss, and
    /// getting it wrong decodes to rubbish. It survives here only as a tie-break
    /// for the one case the run-length test cannot settle - see
    /// <see cref="WhichIsMarkByDuty"/>.</para>
    /// </summary>
    public static class RttySignalAnalyser
    {
        /// <summary>
        /// The speeds that have a name. 45.45 is amateur RTTY and the figure the
        /// IC-7300's own decoder is fixed at; 50 is most European commercial and
        /// weather traffic, DDK9 among them; 56.9 and 74.2 are press circuits; 75
        /// and 100 turn up on press and military traffic.
        ///
        /// <para>This is a list for <see cref="SnapBaud"/> to recognise against,
        /// <b>not</b> the set of speeds the analyser can find. It searches a
        /// continuous range and reports what it measures, because a station on a
        /// speed nobody tabulated is still a station and reporting it as the
        /// nearest famous number would be a lie that decodes to rubbish.</para>
        /// </summary>
        public static readonly double[] StandardBauds = { 45.45, 50.0, 56.9, 74.2, 75.0, 100.0 };

        /// <summary>
        /// The shifts that have a name: 170 amateur and NAVTEX, 200 some military,
        /// 425 weather and press, 450 the German DWD stations, 850 military and
        /// aviation. As with <see cref="StandardBauds"/> this is for recognising a
        /// measurement, not a limit on what can be found - the analyser never
        /// rounds to it, because a measured 450 reported as 425 would hide the very
        /// thing the operator asked to be told.
        /// </summary>
        public static readonly int[] StandardShifts = { 170, 200, 425, 450, 850 };

        /// <summary>
        /// One step of the speed scan. A quarter of a percent is finer than the fit
        /// can resolve, which is the point: the true speed then always lands on the
        /// shoulder of a peak rather than on its summit, and the interpolation has
        /// something to work with.
        /// </summary>
        private const double BaudStep = 1.0025;

        /// <summary>
        /// How far either side of a peak its keying sidebands reach, for the
        /// centroid in <see cref="HighestPeak"/>. 60 Hz covers the keying spread of
        /// the fastest speed considered, and stays comfortably inside half of the
        /// narrowest shift in use - 85 Hz - so the two tones' clusters cannot
        /// overlap and pull each other's centroids together.
        /// </summary>
        private const double ClusterHz = 60;

        /// <summary>
        /// How far down the peak the centroid in <see cref="HighestPeak"/> reaches,
        /// as a fraction of its height above the noise floor. See the note there:
        /// this is what keeps the asymmetrical inter-tone energy out of the sum.
        /// </summary>
        private const double ClusterFloor = 0.30;

        /// <summary>
        /// How much better one way round has to fit than the other before the run
        /// lengths are taken to have settled which tone is mark. Below this the
        /// difference is rounding error and the weak duty-cycle tie-break is used
        /// instead - which is the right outcome for a station sending a one or two
        /// bit stop element, where the two really are indistinguishable.
        /// </summary>
        private const double PolarityMargin = 0.08;

        /// <summary>
        /// Analyse a block of audio. Null when there is nothing in it that looks
        /// like two tones - too short, silent, or only one peak in the passband.
        /// </summary>
        /// <param name="audio">A few seconds. Two is thin, five is comfortable.</param>
        /// <param name="sampleRate">Hz.</param>
        /// <param name="lowHz">Bottom of the search range.</param>
        /// <param name="highHz">Top of the search range.</param>
        /// <param name="lowBaud">Slowest speed to consider.</param>
        /// <param name="highBaud">Fastest speed to consider.</param>
        public static RttySignalEstimate? Analyse(
            ReadOnlySpan<float> audio,
            int sampleRate,
            double lowHz = 300,
            double highHz = 3000,
            double lowBaud = 30,
            double highBaud = 120)
        {
            if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
            if (lowBaud <= 0 || highBaud < lowBaud) throw new ArgumentOutOfRangeException(nameof(lowBaud));

            // 4096 points is about 12 Hz a bin at 48 kHz, which resolves a 170 Hz
            // shift with room to spare. A window that long holds several bits and
            // so sees both tones at once - which is wanted here, unlike in the
            // demodulator where the whole point is to see one bit at a time.
            const int Window = 4096;
            if (audio.Length < Window * 2) return null;

            var spectrum = AverageSpectrum(audio, Window);
            var binHz = (double)sampleRate / Window;

            // The general level of the passband, wanted before the peaks because
            // the centroid that locates each one subtracts it.
            var floor = MedianLevel(spectrum, binHz, lowHz, highHz);

            var first = HighestPeak(spectrum, binHz, lowHz, highHz, floor, exclude: double.NaN);
            if (first is null) return null;

            // At least 80 Hz away: closer than that and it is the skirt of the
            // first peak, not the other tone. The narrowest shift in use is 170.
            var second = HighestPeak(spectrum, binHz, lowHz, highHz, floor,
                                     exclude: first.Value.hz, excludeWidthHz: 80);
            if (second is null) return null;

            var toneA = first.Value.hz;
            var toneB = second.Value.hz;
            var shift = Math.Abs(toneA - toneB);

            // How much the two peaks stand above the general level of the
            // passband. A signal gives tens of dB; noise alone gives a few.
            var prominence = Clamp01(Math.Log10((first.Value.level + second.Value.level) / (2 * floor + 1e-30)) / 1.5);

            var framing = Framing(audio, sampleRate, toneA, toneB, lowBaud, highBaud);
            if (framing is null) return null;

            var (markHz, spaceHz, baud, fit, margin) = framing.Value;

            // Multiplied rather than averaged: both have to be true, and a strong
            // pair of peaks means nothing if the keying in them fits no speed on
            // the list. The polarity margin is deliberately not a factor here -
            // see the note on RttySignalEstimate.Confidence.
            var confidence = prominence * fit;

            return new RttySignalEstimate(markHz, spaceHz, shift, baud, confidence, margin, fit);
        }

        /// <summary>
        /// The nearest speed in <see cref="StandardBauds"/>, or null when the
        /// measurement is not close to any of them - which means the station is on
        /// a speed that is not in the table, and the measured figure is the one to
        /// use and to show.
        /// </summary>
        /// <param name="tolerance">
        /// As a fraction, and it has to be wider than the analyser's own error,
        /// not merely wide enough to keep the named speeds apart. Keeping them
        /// apart is not this number's job: the nearest is picked first, so the
        /// tolerance only ever decides accept-or-reject, never which one.
        ///
        /// <para>Set at 1.5% it was narrower than the measurement it was judging.
        /// Four presses on DDK9, a published 50 baud station, on 2026-10-08 gave
        /// 49.61, 49.71, 50.65 and 50.79 - a spread of +/-1.6%, so the same
        /// station was recognised as 50 on three presses and reported as an odd
        /// speed on the fourth. 2.5% covers that spread with room to spare and
        /// still rejects anything genuinely off the list: the nearest neighbours
        /// to the test cases that must stay unrecognised are 8% and 16% away.</para>
        /// </param>
        public static double? SnapBaud(double measured, double tolerance = 0.025)
        {
            var nearest = StandardBauds.OrderBy(b => Math.Abs(b - measured)).First();
            return Math.Abs(nearest - measured) <= nearest * tolerance ? nearest : null;
        }

        /// <summary>
        /// The nearest shift in <see cref="StandardShifts"/>, or null when the
        /// measurement is not close to any of them - which is itself worth
        /// knowing, since it usually means the two peaks found were not a RTTY
        /// pair at all.
        /// </summary>
        public static int? SnapShift(double measuredHz, double tolerance = 0.08)
        {
            var nearest = StandardShifts.OrderBy(s => Math.Abs(s - measuredHz)).First();
            return Math.Abs(nearest - measuredHz) <= nearest * tolerance ? nearest : null;
        }

        /// <summary>
        /// Magnitude spectrum averaged over as many half-overlapped Hann windows
        /// as the audio holds. Averaging is what makes a steady tone stand out
        /// from noise: the tone adds up in the same bin every time and the noise
        /// does not.
        /// </summary>
        private static double[] AverageSpectrum(ReadOnlySpan<float> audio, int window)
        {
            var bins = new double[window / 2];
            var re = new double[window];
            var im = new double[window];

            var hann = new double[window];
            for (int i = 0; i < window; i++)
                hann[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / window);

            var blocks = 0;
            for (int at = 0; at + window <= audio.Length; at += window / 2)
            {
                for (int i = 0; i < window; i++)
                {
                    re[i] = audio[at + i] * hann[i];
                    im[i] = 0;
                }

                Fft.Transform(re, im);

                for (int i = 0; i < bins.Length; i++)
                    bins[i] += Math.Sqrt(re[i] * re[i] + im[i] * im[i]);

                blocks++;
            }

            if (blocks > 1)
                for (int i = 0; i < bins.Length; i++) bins[i] /= blocks;

            return bins;
        }

        /// <summary>
        /// Where a tone is, to within a hertz or two.
        ///
        /// <para>Not simply the strongest bin. A keyed tone is not a single line:
        /// switching it on and off at the baud rate spreads its energy into keying
        /// sidebands either side of the carrier, and a window this long resolves
        /// them, so the tallest bin is usually a sideband rather than the carrier
        /// itself. Taking it at face value - even with a parabola fitted through
        /// its neighbours - put the answer out by 3 to 6 Hz, worst at the low
        /// sample rates where the bins are fine enough to separate the sidebands
        /// cleanly.</para>
        ///
        /// <para>The sidebands are symmetrical about the carrier, which is the way
        /// out: the centre of gravity of the cluster is the carrier even though no
        /// single bin in it is. Hence an energy-weighted centroid, with the noise
        /// floor subtracted first so a noise pedestal cannot drag it.</para>
        ///
        /// <para>The centroid is taken only over the bins near the top of the peak,
        /// <see cref="ClusterFloor"/> of its height and above, and that is what
        /// makes it work rather than a refinement of it. Keying a tone also throws
        /// low-level energy into the gap between the two tones, and that energy is
        /// <em>not</em> symmetrical about either carrier - it is all on the inner
        /// side. A centroid over everything within a fixed span therefore pulls
        /// both tones towards each other, which measured a 170 Hz shift as 167. Cut
        /// the skirts off and what is left is the symmetrical part. The cut also
        /// makes the span adapt to the signal, since a faster station's cluster is
        /// genuinely wider; <see cref="ClusterHz"/> is only a backstop, to
        /// guarantee the other tone can never be drawn in.</para>
        /// </summary>
        private static (double hz, double level)? HighestPeak(
            double[] bins, double binHz, double lowHz, double highHz, double floor,
            double exclude, double excludeWidthHz = 0)
        {
            var from = Math.Max(1, (int)Math.Ceiling(lowHz / binHz));
            var to = Math.Min(bins.Length - 2, (int)Math.Floor(highHz / binHz));

            var best = -1;
            for (int i = from; i <= to; i++)
            {
                if (!double.IsNaN(exclude) && Math.Abs(i * binHz - exclude) < excludeWidthHz) continue;
                // A local maximum, so that the shoulder of a strong peak cannot be
                // returned as a second tone.
                if (bins[i] < bins[i - 1] || bins[i] < bins[i + 1]) continue;
                if (best < 0 || bins[i] > bins[best]) best = i;
            }

            if (best < 0) return null;

            var span = Math.Max(1, (int)Math.Round(ClusterHz / binHz));
            var lowBin = Math.Max(0, best - span);
            var highBin = Math.Min(bins.Length - 1, best + span);
            var cut = (bins[best] - floor) * ClusterFloor;

            double weighted = 0, weight = 0;
            for (int i = lowBin; i <= highBin; i++)
            {
                var above = bins[i] - floor;
                if (above <= cut) continue;

                // Energy rather than magnitude: it is the squared quantity that is
                // distributed symmetrically about the carrier.
                var w = above * above;
                weighted += i * w;
                weight += w;
            }

            var at = weight > 0 ? weighted / weight : best;
            return (at * binHz, bins[best]);
        }

        private static double MedianLevel(double[] bins, double binHz, double lowHz, double highHz)
        {
            var from = Math.Max(1, (int)Math.Ceiling(lowHz / binHz));
            var to = Math.Min(bins.Length - 1, (int)Math.Floor(highHz / binHz));
            if (to <= from) return 1e-30;

            var slice = new double[to - from + 1];
            Array.Copy(bins, from, slice, 0, slice.Length);
            Array.Sort(slice);
            return Math.Max(slice[slice.Length / 2], 1e-30);
        }

        /// <summary>
        /// The speed and which tone is mark, together, from the run lengths. Null
        /// when there is not enough keying in the audio to judge either.
        /// </summary>
        private static (double markHz, double spaceHz, double baud, double fit, double margin)?
            Framing(ReadOnlySpan<float> audio, int sampleRate, double toneA, double toneB,
                    double lowBaud, double highBaud)
        {
            // One bit at the fastest speed in range, so the filter cannot smear two
            // bits together whatever the signal turns out to be. The speed is not
            // known yet, which is why this cannot use a bit time.
            var window = Math.Max(8, (int)(sampleRate / highBaud));

            // How loud each tone is when it is the one being sent. The two are
            // divided by these before being compared, and that is not a nicety.
            //
            // The comparison decides where one run ends and the next begins, so a
            // comparison between two magnitudes of different scale puts every
            // boundary in the wrong place - one way on the rising edge and the other
            // way on the falling one, which lengthens every run of the louder tone
            // by a constant amount and shortens every run of the quieter one by the
            // same. A constant added to every run is read here as a longer bit, so
            // the speed comes out low, and selective fading on a real HF signal
            // leaves the two tones several dB apart as a matter of course.
            //
            // Measured on 2026-10-08: with the tones matched the scan read a 50 baud
            // signal as 50.02, and with space 1.6 dB louder it read 49.05. The radio
            // was reading 48.7 to 49.2 on a real station at the time, repeatably,
            // and the same bench had already measured that station's two tones 1.6 dB
            // apart. Mark louder reads fast in exactly the same proportion.
            var (levelA, levelB) = ToneLevels(audio, sampleRate, toneA, toneB, window);

            var a = new RttyToneMagnitude(window, toneA, sampleRate);
            var b = new RttyToneMagnitude(window, toneB, sampleRate);

            // A space run is a start element plus however many of the five data
            // bits are space, so between one and six bits. Anything shorter than a
            // third of the fastest possible bit is a noise glitch rather than an
            // element, and anything longer than eight slow bits is an idle line or
            // a gap between transmissions rather than part of a character.
            var shortest = sampleRate / highBaud / 3.0;
            var longest = sampleRate / lowBaud * 8.0;

            var runsA = new List<double>();     // runs during which tone A was on top
            var runsB = new List<double>();
            var onA = true;
            long since = 0;

            for (int i = 0; i < audio.Length; i++)
            {
                var difference = a.Process(audio[i]) / levelA - b.Process(audio[i]) / levelB;
                if (i < window) continue;

                since++;
                var nowA = difference > 0;
                if (nowA == onA) continue;

                if (since >= shortest && since <= longest)
                    (onA ? runsA : runsB).Add(since);

                onA = nowA;
                since = 0;
            }

            if (runsA.Count < 8 || runsB.Count < 8) return null;

            // Scan the whole range rather than a list of famous speeds, so that a
            // station on one of the odd press-circuit rates is measured instead of
            // being rounded to the nearest name. A quarter of a percent a step is
            // finer than the fit can resolve, so the true speed always lands on the
            // shoulder of a peak and the interpolation below can find its summit.
            var steps = (int)Math.Ceiling(Math.Log(highBaud / lowBaud) / Math.Log(BaudStep));
            var fits = new double[steps + 1];
            var polarity = new bool[steps + 1];
            var others = new double[steps + 1];

            for (int i = 0; i <= steps; i++)
            {
                var bit = sampleRate / (lowBaud * Math.Pow(BaudStep, i));

                // Each way round is scored on the runs of the tone it calls space,
                // because those are the ones that should be whole bits.
                var aMark = FitToWholeBits(runsB, bit);
                var bMark = FitToWholeBits(runsA, bit);

                fits[i] = Math.Max(aMark, bMark);
                others[i] = Math.Min(aMark, bMark);
                polarity[i] = bMark > aMark;
            }

            var peak = PickSpeed(fits);
            if (peak < 0) return null;

            // Interpolate across the peak for the fraction of a step between the
            // samples either side of it, which is what turns a scan into a
            // measurement.
            double at = peak;
            if (peak > 0 && peak < steps)
            {
                var denominator = fits[peak - 1] - 2 * fits[peak] + fits[peak + 1];
                if (Math.Abs(denominator) > 1e-12)
                    at += Math.Clamp(0.5 * (fits[peak - 1] - fits[peak + 1]) / denominator, -0.5, 0.5);
            }

            var bestBaud = lowBaud * Math.Pow(BaudStep, at);
            var bestFit = fits[peak];
            var bIsMark = polarity[peak];

            // How much better the winning polarity fitted than the other one. On a
            // station using a one or two bit stop element the two come out the same,
            // because then every run is a whole number of bits and the asymmetry
            // this rests on is simply absent - so fall back to the duty cycle, weak
            // as it is, rather than pick a polarity by rounding error.
            var margin = bestFit <= 0 ? 0 : (bestFit - others[peak]) / bestFit;
            if (margin < PolarityMargin)
            {
                var duty = WhichIsMarkByDuty(runsA, runsB);
                bIsMark = duty.bIsMark;
                margin = Math.Min(duty.margin, PolarityMargin);   // claims no more than it has
            }

            return bIsMark
                ? (toneB, toneA, bestBaud, bestFit, margin)
                : (toneA, toneB, bestBaud, bestFit, margin);
        }

        /// <summary>
        /// How loud each of the two tones is while it is the one being sent, so that
        /// they can be compared on equal terms.
        ///
        /// <para>Two passes, because the answer is needed before the boundaries are
        /// known and the boundaries are what it is for. The first pass asks only
        /// which tone is the louder at each instant, which no scaling can change the
        /// sense of for most of a run, and takes the median of each tone's magnitude
        /// over the samples where it won. A median, so that the edges - where both
        /// filters are part way between one tone and the other, and which are the
        /// samples the first pass places wrongly - cannot move it.</para>
        ///
        /// <para>The ratio is capped at 20 dB. Past that the quieter tone is more
        /// likely absent than faded, and scaling noise up to meet a carrier would
        /// manufacture keying out of nothing; capped, a signal that is not RTTY at
        /// all goes on failing to fit, which is what the confidence is for.</para>
        /// </summary>
        private static (double a, double b) ToneLevels(
            ReadOnlySpan<float> audio, int sampleRate, double toneA, double toneB, int window)
        {
            var a = new RttyToneMagnitude(window, toneA, sampleRate);
            var b = new RttyToneMagnitude(window, toneB, sampleRate);

            var onA = new List<double>();
            var onB = new List<double>();

            for (int i = 0; i < audio.Length; i++)
            {
                var magA = a.Process(audio[i]);
                var magB = b.Process(audio[i]);
                if (i < window) continue;
                (magA > magB ? onA : onB).Add(magA > magB ? magA : magB);
            }

            // Neither tone can be calibrated from a handful of samples, and a tone
            // that is hardly ever on top is not a tone. Leave both alone and let the
            // fit say what it thinks.
            var floor = Math.Max(16, (audio.Length - window) / 40);     // 2.5%
            if (onA.Count < floor || onB.Count < floor) return (1, 1);

            var levelA = Median(onA);
            var levelB = Median(onB);
            if (levelA <= 0 || levelB <= 0) return (1, 1);

            var cap = Math.Pow(10, 20 / 20.0);
            if (levelA > levelB * cap) levelA = levelB * cap;
            if (levelB > levelA * cap) levelB = levelA * cap;

            return (levelA, levelB);
        }

        private static double Median(List<double> values)
        {
            values.Sort();
            return values[values.Count / 2];
        }

        /// <summary>
        /// Which peak of the scan is the speed: the <b>slowest</b> one that fits
        /// about as well as the best does, not the best.
        ///
        /// <para>That is not caution, it is necessary. A bit period half the true
        /// one divides everything exactly too, since every whole number of bits is
        /// also a whole number of half-bits, so the scan has a second peak at double
        /// the real speed which is every bit as convincing - sometimes a shade
        /// better. It does not go the other way: a half-speed yardstick leaves
        /// half-bit remainders all over the runs, so the slow end is rejected on its
        /// own merits wherever it is genuinely wrong, and nothing real is lost by
        /// preferring it. Taking the best fit instead read every 50 baud weather
        /// station as 100, and read its tones the wrong way round into the bargain,
        /// because at double speed the stop element stops being half a bit.</para>
        ///
        /// <para>The slower candidates are looked for <b>only at exact submultiples
        /// of the winning speed</b>, because that is the one place an alias can be.
        /// This used to walk up from the slow end and take the first local maximum
        /// within the slack, which is the same answer on synthetic audio and wrong
        /// off the air: a real fit curve is rippled by noise and has a local maximum
        /// every few steps, so the walk stopped on a ripple short of the summit. The
        /// error was always in the same direction, which is how it showed - four
        /// readings of a 50 baud station on 2026-10-08 came out 48.53, 48.86, 48.92
        /// and 49.31, never once above, where synthetic audio had given 50.0 to
        /// within half a percent.</para>
        /// </summary>
        private static int PickSpeed(double[] fits)
        {
            var best = 0;
            for (int i = 1; i < fits.Length; i++)
                if (fits[i] > fits[best]) best = i;
            if (fits[best] <= 0) return -1;

            var good = fits[best] - 0.05;   // measurement scatter, not a real difference

            // Its position is exact, so a narrow window around each submultiple is
            // enough: wide enough to find the summit through the scatter, too narrow
            // to wander off onto a neighbouring bump.
            var window = (int)Math.Ceiling(Math.Log(1.015) / Math.Log(BaudStep));

            // An alias sits at a whole multiple of the true speed, so the true speed
            // sits at a whole division of this one and nowhere else is worth looking.
            var chosen = best;
            for (int k = 2; k <= 6; k++)
            {
                var at = best - (int)Math.Round(Math.Log(k) / Math.Log(BaudStep));
                if (at < 0) break;          // this and every slower one are off the
                                            // bottom of the search range

                var lo = Math.Max(0, at - window);
                var hi = Math.Min(fits.Length - 1, at + window);
                var local = lo;
                for (int i = lo; i <= hi; i++)
                    if (fits[i] > fits[local]) local = i;

                if (fits[local] >= good && local < chosen) chosen = local;
            }

            return chosen;
        }

        /// <summary>
        /// Which tone is mark, by which is on the air for longer. The tie-break of
        /// last resort: see the note on the class about why it is nearly useless on
        /// plain text, and why it is still worth having for the station whose run
        /// lengths cannot decide. True when tone B is mark.
        /// </summary>
        private static (bool bIsMark, double margin) WhichIsMarkByDuty(
            List<double> runsA, List<double> runsB)
        {
            var onA = runsA.Sum();
            var onB = runsB.Sum();
            var total = onA + onB;
            if (total <= 0) return (false, 0);

            return (onB > onA, Math.Abs(onA - onB) / total);
        }

        /// <summary>
        /// How nearly every run is a whole number of bits long: 1 for a perfect
        /// fit, 0 for no relationship at all. Weighted by length, because a 6%
        /// speed error shows up on a six-bit run and is lost in the noise on a
        /// one-bit one.
        /// </summary>
        private static double FitToWholeBits(List<double> runs, double bit)
        {
            double weighted = 0, weight = 0;

            foreach (var run in runs)
            {
                var bits = run / bit;
                if (bits < 0.5 || bits > 13) continue;

                var error = Math.Abs(bits - Math.Round(bits));

                // Half a bit out is as wrong as it is possible to be, so scale to
                // that and the score runs over the full range.
                weighted += bits * (1 - Math.Min(error, 0.5) / 0.5);
                weight += bits;
            }

            return weight == 0 ? 0 : weighted / weight;
        }

        private static double Clamp01(double x) => double.IsNaN(x) ? 0 : Math.Clamp(x, 0, 1);
    }
}
