using System.Text.RegularExpressions;

namespace YaesuWebControl.Tests;

/// <summary>
/// An apostrophe inside a single-quoted string in a page's inline script ends
/// the string, and the whole script fails to parse: in v2.5.6-pre5 the menu
/// title 'The radio's USB audio...' left /radio and /rtty-contest blank. The
/// build can't see it, because Razor passes script text through unchecked.
/// </summary>
public class InlineScriptQuoteTests
{
    // A single-quoted value whose quote is followed by a letter: 'The radio's ...
    private static readonly Regex BrokenString =
        new(@"(?:[:=(,]|\breturn)\s*'[^'\r\n]*[A-Za-z]'[a-z]", RegexOptions.Compiled);

    private static readonly Regex Script =
        new(@"<script\b[^>]*>(?<body>.*?)</script>", RegexOptions.Singleline | RegexOptions.Compiled);

    [Fact]
    public void No_inline_script_has_an_apostrophe_inside_a_single_quoted_string()
    {
        var root = RepoRoot();
        var bad = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "Pages"), "*.cshtml", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in Script.Matches(text))
                foreach (var line in m.Groups["body"].Value.Split('\n'))
                    if (BrokenString.IsMatch(line))
                        bad.Add($"{Path.GetRelativePath(root, file)}: {line.Trim()}");
        }
        Assert.True(bad.Count == 0, "Use double quotes for these strings:\n" + string.Join("\n", bad));
    }

    [Theory]
    [InlineData("{ title: 'The radio's USB audio' }", true)]
    [InlineData("{ title: \"The radio's USB audio\" }", false)]
    [InlineData("{ label: 'Audio Waterfall', href: '/audio-waterfall' }", false)]
    [InlineData("const s = 'It\\'s escaped';", false)]
    public void The_check_finds_the_pre5_mistake(string line, bool broken) =>
        Assert.Equal(broken, BrokenString.IsMatch(line));

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Yaesu_Web_Control.csproj"))) return dir.FullName;
        throw new DirectoryNotFoundException($"No Yaesu_Web_Control.csproj above {AppContext.BaseDirectory}.");
    }
}
