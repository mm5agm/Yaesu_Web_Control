using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaesu_Web_Control.Models;
using Yaesu_Web_Control.Services;

namespace YaesuWebControl.Tests
{
    /// <summary>
    /// The server names the band from the operator's own region. Until this fix
    /// it never read the region at all: the "checked recently" test overflowed on
    /// the first call, so every operator got Region 1 and, for example, a US
    /// operator on 3.9 MHz was out of band (found in IWC #137, same code here).
    /// These run against the built-in edges (no web root), which match
    /// bandplan.default.json for the bands used here.
    /// </summary>
    public class BandPlanRegionTests
    {
        private static BandPlanService Service(string bandPlan) =>
            new(new NoWebRoot(), new FakeSettingsService(bandPlan), NullLogger<BandPlanService>.Instance);

        [Theory]
        [InlineData("Region1", "Region1")]
        [InlineData("Region2", "Region2")]
        [InlineData("Region3", "Region3")]
        [InlineData("USA", "Region2")]
        [InlineData("UK", "Region1")]
        public void TheConfiguredRegionIsUsedFromTheFirstCall(string setting, string expected)
        {
            Assert.Equal(expected, Service(setting).CurrentRegion);
        }

        [Theory]
        [InlineData("Region2", 146_200_000L, "2m")]
        [InlineData("Region2", 3_900_000L, "80m")]
        [InlineData("Region1", 146_200_000L, BandPlanService.UnknownBand)]
        [InlineData("Region1", 3_900_000L, BandPlanService.UnknownBand)]
        public void TheBandComesFromTheConfiguredRegion(string setting, long hz, string expected)
        {
            Assert.Equal(expected, Service(setting).BandForFrequency(hz));
        }

        private sealed class NoWebRoot : IWebHostEnvironment
        {
            public string WebRootPath { get; set; } = "";
            public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
            public string ApplicationName { get; set; } = "tests";
            public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
            public string ContentRootPath { get; set; } = "";
            public string EnvironmentName { get; set; } = "Test";
        }

        private sealed class FakeSettingsService : ISettingsService
        {
            private ApplicationSettings _settings;
            public FakeSettingsService(string bandPlan) => _settings = new ApplicationSettings { BandPlan = bandPlan };
            public Task<ApplicationSettings> GetSettingsAsync() => Task.FromResult(_settings);
            public Task SaveSettingsAsync(ApplicationSettings settings) { _settings = settings; return Task.CompletedTask; }
            public ApplicationSettings GetCachedSettings() => _settings;
            public string GetSettingsFilePath() => Path.Combine(Path.GetTempPath(), "ywc-test-settings.json");
            public void InvalidateCache() { }
            public void Dispose() { }
        }
    }
}
