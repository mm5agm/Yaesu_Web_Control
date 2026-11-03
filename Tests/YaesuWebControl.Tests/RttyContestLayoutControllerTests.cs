using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using RadioWebControl.Core.Models;
using RadioWebControl.Core.Services.Flex;
using Xunit;
using Yaesu_Web_Control.Controllers;

namespace YaesuWebControl.Tests
{
    /// <summary>
    /// The RTTY Contest view keeps its named layouts with Experimental's API
    /// and store, but at its own route and in its own file, so the two sets
    /// never mix. Both controllers inherit LayoutStoreController; these check
    /// the two things that differ, and that neither answers the other's URL.
    /// </summary>
    public class RttyContestLayoutControllerTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "ywc-rc-layouts-" + Guid.NewGuid().ToString("N"));

        public RttyContestLayoutControllerTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        [Fact]
        public void HasItsOwnRoute()
        {
            var routes = typeof(RttyContestLayoutController)
                .GetCustomAttributes<RouteAttribute>(inherit: true)
                .Select(r => r.Template)
                .ToArray();
            Assert.Equal(new[] { "api/rttycontestlayouts" }, routes);

            var flexRoutes = typeof(FlexLayoutController)
                .GetCustomAttributes<RouteAttribute>(inherit: true)
                .Select(r => r.Template)
                .ToArray();
            Assert.Equal(new[] { "api/flexlayouts" }, flexRoutes);
        }

        [Fact]
        public async Task SavesIntoTheKeyedStoreNotExperimentals()
        {
            var flex = new FlexLayoutStore(Path.Combine(_dir, "flex-layouts.json"));
            var contest = new FlexLayoutStore(Path.Combine(_dir, "rtty-contest-layouts.json"));
            var services = new ServiceCollection()
                .AddSingleton<IFlexLayoutStore>(flex)
                .AddKeyedSingleton<IFlexLayoutStore>(RttyContestLayoutController.StoreKey, contest)
                .BuildServiceProvider();

            var controller = ActivatorUtilities.CreateInstance<RttyContestLayoutController>(services);
            var result = await controller.Create(new LayoutStoreController.CreateRequest
            {
                Name = "RTTY contest",
                Layout = new JsonObject { ["tunerWidth"] = 420 },
            }, CancellationToken.None);

            Assert.IsType<OkObjectResult>(result);
            Assert.Contains(contest.GetSnapshot().Layouts, l => l.Name == "RTTY contest");
            Assert.DoesNotContain(flex.GetSnapshot().Layouts, l => l.Name == "RTTY contest");
        }
    }
}
