using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Mvc;

namespace YaesuWebControl.Tests;

// The tests load the net10.0 build: the one Docker and macOS run. Every
// controller in it must find its services there, or its whole API fails with
// "Unable to resolve service" (ScopeController after pre18: ScopeAfFftMemory
// was registered only inside #if WINDOWS, so the scope API failed in Docker).
public class CrossPlatformRegistrationTests
{
    private static string RepoFile(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, name)))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, name);
    }

    // Program.cs with every #if WINDOWS block taken out (its #else kept).
    private static string ProgramOutsideWindowsBlocks()
    {
        var kept = new StringBuilder();
        var stack = new Stack<bool>();   // true = this level is dropped
        foreach (var raw in File.ReadAllLines(RepoFile("Program.cs")))
        {
            var line = raw.Trim();
            if (line.StartsWith("#if"))
            {
                var parentDropped = stack.Count > 0 && stack.Peek();
                stack.Push(parentDropped || line == "#if WINDOWS");
                continue;
            }
            if (line.StartsWith("#else") && stack.Count > 0)
            {
                var dropped = stack.Pop();
                var parentDropped = stack.Count > 0 && stack.Peek();
                stack.Push(parentDropped || !dropped);
                continue;
            }
            if (line.StartsWith("#endif") && stack.Count > 0) { stack.Pop(); continue; }
            if (stack.Count == 0 || !stack.Peek()) kept.AppendLine(raw);
        }
        return kept.ToString();
    }

    [Fact]
    public void AppServicesControllersNeed_AreRegisteredOnEveryPlatform()
    {
        var program = ProgramOutsideWindowsBlocks();
        var app = typeof(Yaesu_Web_Control.Services.ScopeAfFftMemory).Assembly;
        var missing = new List<string>();

        foreach (var controller in app.GetTypes().Where(t => !t.IsAbstract && typeof(ControllerBase).IsAssignableFrom(t)))
        {
            foreach (var ctor in controller.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
            {
                foreach (var p in ctor.GetParameters())
                {
                    var t = p.ParameterType;
                    // Only the app's own concrete classes: interfaces, framework
                    // and core services are registered in ways a name search can't see.
                    if (t.Assembly != app || t.IsInterface || t.IsAbstract) continue;
                    if (!program.Contains(t.Name))
                        missing.Add($"{controller.Name} needs {t.Name}");
                }
            }
        }

        Assert.Empty(missing);
    }
}
