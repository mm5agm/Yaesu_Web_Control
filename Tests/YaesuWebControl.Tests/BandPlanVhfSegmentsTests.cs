using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace YaesuWebControl.Tests
{
    /// <summary>
    /// 2 m and 70 cm had band edges but no segments, so the Segment drop-down
    /// showed "--" on the FT-817/857/897/991A, and in IWC on the IC-9700 (found
    /// while answering Felix about the FT-817, 2026-11-20). Every region now has
    /// both bands, in the JSON overlay and in the JS defaults it overrides, and
    /// every segment sits inside that region's own band edges.
    /// </summary>
    public class BandPlanVhfSegmentsTests
    {
        private static readonly string[] Regions = { "Region1", "Region2", "Region3", "Japan" };

        [Fact]
        public void EveryRegionHas2mAnd70cmSegmentsInsideItsBandEdges()
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(LocateRepoPath("wwwroot/bandplan.default.json")));
            var root = doc.RootElement;

            foreach (var region in Regions)
            {
                var edges = root.GetProperty("bandEdges").GetProperty(region).EnumerateArray().ToList();
                foreach (var band in new[] { "2m", "70cm" })
                {
                    var segments = root.GetProperty("bandPlans").GetProperty(region).GetProperty(band);
                    var edge = edges.Single(e => e.GetProperty("name").GetString() == band);
                    long lo = edge.GetProperty("lo").GetInt64(), hi = edge.GetProperty("hi").GetInt64();

                    Assert.True(segments.EnumerateObject().Any(), $"{region} {band} has no segments");
                    foreach (var s in segments.EnumerateObject())
                    {
                        long f = s.Value.GetProperty("freq").GetInt64();
                        Assert.True(f >= lo && f <= hi, $"{region} {band} {s.Name} {f} is outside {lo}-{hi}");
                    }
                }
            }
        }

        [Fact]
        public void TheJsDefaultsHaveThemToo()
        {
            string js = File.ReadAllText(LocateRepoPath("wwwroot/js/ui/band-plan.js"));
            string plans = js.Substring(0, js.IndexOf("export const BAND_EDGES", StringComparison.Ordinal));

            Assert.Equal(Regions.Length, Regex.Matches(plans, @"'2m':\s*\{").Count);
            Assert.Equal(Regions.Length, Regex.Matches(plans, @"'70cm':\s*\{").Count);
        }

        private static string LocateRepoPath(string relative)
        {
            string native = relative.Replace('/', Path.DirectorySeparatorChar);
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                string candidate = Path.Combine(dir.FullName, native);
                if (File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            throw new FileNotFoundException($"Could not find {relative} walking up from {AppContext.BaseDirectory}");
        }
    }
}
