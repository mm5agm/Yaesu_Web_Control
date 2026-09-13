using System.Text.RegularExpressions;
using NuStreamDocs.Building;
using NuStreamDocs.Config.MkDocs;
using NuStreamDocs.Highlight;
using NuStreamDocs.Links;
using NuStreamDocs.MarkdownExtensions;
using NuStreamDocs.Nav;
using NuStreamDocs.Search.Lunr;
using NuStreamDocs.Theme.Material;
using NuStreamDocs.Toc;
using UserManual;

// Generate the operator manual as a static Material-themed book.
//
// Invoked from the repo root (MSBuild WorkingDirectory, or `dotnet run
// --project tools/UserManual` with cwd = repo root):
//
//   dotnet run --project tools/UserManual -- --input user-manual --output wwwroot/manual
//
// Paths are resolved against cwd, not AppContext.BaseDirectory (the latter
// is the generator's own bin folder and would write into tools/).

var argsList = args.ToList();
var input = GetOption(argsList, "--input") ?? "user-manual";
var output = GetOption(argsList, "--output") ?? "wwwroot/manual";
var mkdocs = GetOption(argsList, "--mkdocs") ?? "mkdocs.yml";

var cwd = Directory.GetCurrentDirectory();
var inputPath = Path.GetFullPath(Path.Combine(cwd, input));
var outputPath = Path.GetFullPath(Path.Combine(cwd, output));
var mkdocsPath = Path.GetFullPath(Path.Combine(cwd, mkdocs));

if (!Directory.Exists(inputPath))
{
    Console.Error.WriteLine($"Input folder not found: {inputPath}");
    return 1;
}

if (!File.Exists(mkdocsPath))
{
    Console.Error.WriteLine($"mkdocs.yml not found: {mkdocsPath}");
    return 1;
}

Directory.CreateDirectory(outputPath);

var pages = await new DocBuilder()
    .WithInput(inputPath)
    .WithOutput(outputPath)
    .UseMkDocsConfig(mkdocsPath)
    .Exclude("**/404.md")
    // Flat foo.html next to pictures/ so relative image hrefs resolve under /manual/.
    .UseDirectoryUrls(false)
    .UseMarkdownLinks(false)
    .UseMaterialTheme(opts => opts
        .WithSiteName("Yaesu Web Control"u8)
        .WithCopyright("© 2026 MM5AGM"u8)
        .WithRepoUrl("https://github.com/mm5agm/Yaesu_Web_Control"u8)
        .WithEditUri("edit/develop/user-manual/"u8)
        .WithEmbeddedAssetRoot("assets"u8))
    .UseNav(opts => opts.FromMkDocsYaml(mkdocsPath) with { Prune = true, SortBy = NuStreamDocs.Nav.NavSortBy.None })
    .UseToc()
    .UseHighlight()
    .UseLunrSearch()
    .UseCommonMarkdownExtensions()
    .UsePlugin(new BlockQuotePlugin())
    .UsePlugin(new BackToAppPlugin())
    .BuildAsync();

Console.WriteLine($"Built {pages} page(s) into {outputPath}.");

// NuStreamDocs may emit a 404.md into the docs input; that file is not a chapter.
var stray404 = Path.Combine(inputPath, "404.md");
if (File.Exists(stray404))
    File.Delete(stray404);

// Lunr locations are root-absolute ("/introduction.html"). The book is served
// from /manual/, so strip the leading slash so search hits stay in the book.
var searchIndex = Path.Combine(outputPath, "search", "search_index.json");
if (File.Exists(searchIndex))
{
    var json = File.ReadAllText(searchIndex);
    json = json.Replace("\"location\":\"/", "\"location\":\"");
    File.WriteAllText(searchIndex, json);
    var gz = searchIndex + ".gz";
    if (File.Exists(gz))
        File.Delete(gz);
}

// Theme nav/footer hrefs are root-absolute (/introduction.html). The book is
// served from /manual/, so drop the leading slash. Leave href="/" (back to app).
foreach (var htmlFile in Directory.EnumerateFiles(outputPath, "*.html"))
{
    var html = File.ReadAllText(htmlFile);
    var updated = FinishShippedHtml(html);
    if (updated != html)
        File.WriteAllText(htmlFile, updated);
}

return 0;

static string? GetOption(List<string> list, string name)
{
    var i = list.IndexOf(name);
    if (i < 0 || i + 1 >= list.Count) return null;
    return list[i + 1];
}

static string FinishShippedHtml(string html)
{
    html = new Regex(@"href=""/(?<page>[A-Za-z0-9._\-]+\.html(?:#[^""]*)?)""").Replace(html, @"href=""${page}""");
    html = html.Replace(
        "content=\"/search/search_index.json\"",
        "content=\"search/search_index.json\"");

    // Theme emits the search toggle and the query box with the same id.
    html = Regex.Replace(
        html,
        "id=\"__search\"(\\s+)name=\"query\"",
        "id=\"__search-query\"$1name=\"query\"");

    html = html.Replace(
        """<span class="md-search__icon" aria-hidden="true"><i class="fa-solid fa-magnifying-glass"></i></span>""",
        """<label class="md-search__icon" for="__search" title="Search" aria-label="Search"><i class="fa-solid fa-magnifying-glass"></i></label>""");

    if (!html.Contains("data-md-component=\"search-list\"") && html.Contains("data-md-component=\"search-query\""))
    {
        const string panel =
            """
                <div class="md-search__output">
                    <div class="md-search__scrollwrap">
                        <div class="md-search-result">
                            <div class="md-search-result__meta" data-md-component="search-status"></div>
                            <ol class="md-search-result__list" data-md-component="search-list"></ol>
                        </div>
                    </div>
                </div>
            """;
        html = html.Replace("    </div>\r\n</form>", "    </div>\n" + panel + "</form>");
        html = html.Replace("    </div>\n</form>", "    </div>\n" + panel + "</form>");
    }

    if (!html.Contains("ywc-search-shell") && html.Contains("data-md-component=\"search-query\""))
    {
        const string css =
            """
            <style id="ywc-search-shell">
            [data-md-toggle="search"]:checked ~ .md-container .md-search__inner { opacity: 1; width: 18rem; }
            [data-md-toggle="search"]:checked ~ .md-container .md-search__output { opacity: 1; box-shadow: var(--md-shadow-z3); }
            [data-md-toggle="search"]:checked ~ .md-container .md-search__scrollwrap { max-height: 75vh; }
            .md-search__item { list-style: none; margin: 0; padding: 0; }
            .md-search__link { display: block; padding: .55rem .8rem; color: inherit; }
            .md-search__title { display: block; font-weight: 700; }
            .md-search__path { display: block; opacity: .55; font-size: .75rem; }
            .md-search__excerpt { display: block; font-size: .85rem; }
            .md-search__highlight { background: #ffe57f; }
            </style>
            """;
        html = html.Replace("</head>", css + "</head>");
    }

    return html;
}

