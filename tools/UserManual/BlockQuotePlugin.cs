using System.Buffers;
using System.Text;
using NuStreamDocs.Plugins;

namespace UserManual;

/// <summary>
/// NuStreamDocs 1.6.1 classifies blockquotes but emits them as escaped
/// paragraph text (<c>&gt; …</c>). Rewrite CommonMark <c>&gt;</c> quotes
/// into pymdown admonitions so the Material theme can style them.
/// </summary>
internal sealed class BlockQuotePlugin : IPlugin, IPagePreRenderPlugin
{
    private static readonly byte[] NameBytes = "ywc-blockquotes"u8.ToArray();

    public ReadOnlySpan<byte> Name => NameBytes;

    public PluginPriority PreRenderPriority => new(PluginBand.Earliest, 0);

    public bool NeedsRewrite(ReadOnlySpan<byte> markdown) =>
        markdown.IndexOf((byte)'>') >= 0;

    public void PreRender(in PagePreRenderContext context)
    {
        var source = Encoding.UTF8.GetString(context.Source);
        context.Output.Write(Encoding.UTF8.GetBytes(Rewrite(source)));
    }

    internal static string Rewrite(string source)
    {
        var lines = source.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder(source.Length + 64);
        var inFence = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.StartsWith("```", StringComparison.Ordinal))
                inFence = !inFence;

            if (inFence || !IsQuote(line))
            {
                sb.Append(line);
                if (i < lines.Length - 1)
                    sb.Append('\n');
                continue;
            }

            sb.Append("!!! note\n");
            while (i < lines.Length && IsQuote(lines[i]))
            {
                var body = QuoteBody(lines[i]);
                if (body.Length == 0)
                {
                    // A 4-space blank line is indented code in CommonMark.
                    sb.Append('\n');
                }
                else
                {
                    sb.Append("    ");
                    sb.Append(body);
                    sb.Append('\n');
                }
                i++;
            }

            // The loop advanced past the quote; reprocess the current line.
            i--;
            if (i < lines.Length - 1 && lines[i + 1].Length > 0)
                sb.Append('\n');
        }

        return sb.ToString();
    }

    private static bool IsQuote(string line)
    {
        if (line.Length == 0) return false;
        var i = 0;
        while (i < line.Length && line[i] == ' ') i++;
        if (i >= line.Length || line[i] != '>') return false;
        return i == 0 || i <= 3; // CommonMark allows up to 3 leading spaces
    }

    private static string QuoteBody(string line)
    {
        var i = 0;
        while (i < line.Length && line[i] == ' ') i++;
        if (i >= line.Length || line[i] != '>') return line;
        i++;
        if (i < line.Length && line[i] == ' ') i++;
        return line[i..];
    }
}
