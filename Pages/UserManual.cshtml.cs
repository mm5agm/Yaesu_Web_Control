using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.AutoIdentifiers;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Yaesu_Web_Control.Pages
{
    /// <summary>
    /// Renders the USER_MANUAL.md file from the project root as HTML on each
    /// request. Single source of truth — every edit to the markdown shows up
    /// in the in-app manual immediately, and the GitHub-rendered version
    /// stays in lockstep with no duplicate-maintenance burden.
    /// </summary>
    public class UserManualModel : PageModel
    {
        private readonly IWebHostEnvironment _env;
        private readonly ILogger<UserManualModel> _logger;

        // Built once at startup. Markdig pipelines are immutable and
        // thread-safe, so we keep a static instance.
        private static readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()                                       // tables, task lists, etc.
            .UseAutoIdentifiers(AutoIdentifierOptions.GitHub)
            .UseEmojiAndSmiley()
            .Build();

        // Convert "src=\"pictures/foo.png\"" → "src=\"/pictures/foo.png\""
        // and same for href. Without the leading slash the browser resolves
        // relative to /UserManual and the request 404s. The pictures/ folder
        // is served from a dedicated static-file provider at /pictures (see
        // Program.cs).
        private static readonly Regex PicturesPathFix = new(
            @"(?<attr>(?:src|href)=)""pictures/",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Match every heading tag (with or without an existing id attribute)
        // so we can rewrite the id to a strictly-GitHub-compatible slug.
        // Markdig's GitHub auto-identifier is close but doesn't reliably
        // match for headings containing dots ("5.8" → GitHub strips dots and
        // produces "58", Markdig may emit "5-8"). The TOC inside
        // USER_MANUAL.md uses GitHub anchors throughout, so we standardise
        // on that.
        private static readonly Regex HeadingPattern = new(
            "<(?<tag>h[1-6])(?<attrs>[^>]*)>(?<inner>.*?)</h[1-6]>",
            RegexOptions.Compiled | RegexOptions.Singleline);
        private static readonly Regex StripIdAttr = new(
            @"\s*id=""[^""]*""",
            RegexOptions.Compiled);
        private static readonly Regex StripTags = new("<[^>]+>", RegexOptions.Compiled);

        public UserManualModel(IWebHostEnvironment env, ILogger<UserManualModel> logger)
        {
            _env = env;
            _logger = logger;
        }

        public string ManualHtml { get; private set; } = "";
        public string LoadError  { get; private set; } = "";

        public void OnGet()
        {
            var path = Path.Combine(_env.ContentRootPath, "USER_MANUAL.md");
            if (!System.IO.File.Exists(path))
            {
                LoadError = $"USER_MANUAL.md not found at {path}.";
                return;
            }
            try
            {
                var markdown = System.IO.File.ReadAllText(path);
                var html = Markdown.ToHtml(markdown, _pipeline);
                html = PicturesPathFix.Replace(html, "${attr}\"/pictures/");
                html = RewriteHeadingIds(html);
                ManualHtml = html;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to render USER_MANUAL.md");
                LoadError = $"Failed to render the manual: {ex.Message}";
            }
        }

        /// <summary>
        /// Replace every heading's id attribute with a strict GitHub-style
        /// slug, so the TOC links inside USER_MANUAL.md (which use GitHub
        /// anchors) land on the right place.
        /// </summary>
        private static string RewriteHeadingIds(string html)
        {
            return HeadingPattern.Replace(html, m =>
            {
                var tag    = m.Groups["tag"].Value;
                var attrs  = StripIdAttr.Replace(m.Groups["attrs"].Value, "");
                var inner  = m.Groups["inner"].Value;
                // Decode entities before slugging: "Backup & Restore" arrives as
                // "&amp;", and GitHub slugs the ampersand away, not "amp".
                var text   = System.Net.WebUtility.HtmlDecode(StripTags.Replace(inner, ""));
                var id     = GitHubSlug(text);
                return $"<{tag} id=\"{id}\"{attrs}>{inner}</{tag}>";
            });
        }

        /// <summary>
        /// Reproduce GitHub's heading-anchor slug algorithm:
        ///   - Lowercase
        ///   - Strip every character that isn't a letter, digit, hyphen or
        ///     space (dots, commas, brackets etc. are removed, NOT replaced)
        ///   - Replace every space with a hyphen — each one, not each run.
        ///     "Scope — the" loses its em-dash and keeps both spaces, so
        ///     GitHub makes "scope--the"; collapsing the run to one hyphen
        ///     broke nine TOC links in the app (#158) while GitHub's own
        ///     rendering of the same file was fine
        ///   - Trim leading / trailing hyphens
        /// </summary>
        public static string GitHubSlug(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var sb = new StringBuilder(text.Length);
            foreach (var ch in text)
            {
                if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
                else if (ch == '-') sb.Append(ch);
                else if (ch == ' ') sb.Append('-');
                // everything else — punctuation, dots, etc. — is dropped
            }
            return sb.ToString().Trim('-');
        }
    }
}
