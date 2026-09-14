using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Yaesu_Web_Control.Services.Audio;

namespace Yaesu_Web_Control.Controllers
{
    /// <summary>
    /// Diagnostic tap on the RX audio the app actually has, taken at the same
    /// point the CW decoder reads it - after stereo-to-mono, RX gain and any
    /// resampling, but before the codec. Comparing this against a capture of
    /// the same signal taken straight off the sound device with ffmpeg says
    /// whether the capture chain is damaging the audio or whether the fault is
    /// downstream in the codec or the browser.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public sealed class AudioTapController : ControllerBase
    {
        private readonly RadioAudioBridgeService _bridge;

        public AudioTapController(RadioAudioBridgeService bridge) => _bridge = bridge;

        [HttpGet("record")]
        public async Task<IActionResult> Record([FromQuery] int seconds = 20, CancellationToken ct = default)
        {
            seconds = Math.Clamp(seconds, 1, 120);
            var samples = new List<float>(seconds * AudioConstants.SampleRate + 4096);
            var gate = new object();

            void OnFrame(ReadOnlyMemory<float> frame)
            {
                lock (gate) samples.AddRange(frame.ToArray());
            }

            _bridge.RxFrameCaptured += OnFrame;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), ct);
            }
            finally
            {
                _bridge.RxFrameCaptured -= OnFrame;
            }

            float[] pcm;
            lock (gate) pcm = samples.ToArray();

            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MM5AGM", "Yaesu Web Control");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"tap-{DateTime.Now:yyyyMMdd-HHmmss}.wav");
            WriteWav(path, pcm, AudioConstants.SampleRate);

            double peak = 0, sumSq = 0;
            foreach (var v in pcm) { peak = Math.Max(peak, Math.Abs(v)); sumSq += (double)v * v; }
            double rms = pcm.Length > 0 ? Math.Sqrt(sumSq / pcm.Length) : 0;

            return Ok(new
            {
                path,
                samples = pcm.Length,
                seconds = pcm.Length / (double)AudioConstants.SampleRate,
                codec = _bridge.ActiveCodec,
                rxGain = _bridge.RxGain,
                peakDb = 20 * Math.Log10(Math.Max(peak, 1e-12)),
                rmsDb = 20 * Math.Log10(Math.Max(rms, 1e-12))
            });
        }

        private static void WriteWav(string path, float[] pcm, int rate)
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            using var w = new BinaryWriter(fs);
            int dataBytes = pcm.Length * 2;
            w.Write(new[] { 'R', 'I', 'F', 'F' });
            w.Write(36 + dataBytes);
            w.Write(new[] { 'W', 'A', 'V', 'E', 'f', 'm', 't', ' ' });
            w.Write(16);
            w.Write((short)1);
            w.Write((short)1);
            w.Write(rate);
            w.Write(rate * 2);
            w.Write((short)2);
            w.Write((short)16);
            w.Write(new[] { 'd', 'a', 't', 'a' });
            w.Write(dataBytes);
            foreach (var v in pcm)
                w.Write((short)Math.Clamp((int)MathF.Round(v * 32767f), short.MinValue, short.MaxValue));
        }
    }
}
