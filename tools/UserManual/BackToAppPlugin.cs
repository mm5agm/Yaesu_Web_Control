using System.Buffers;
using System.Text;
using NuStreamDocs.Plugins;

namespace UserManual;

/// <summary>
/// Injects a "Back to Yaesu Web Control" bar so operators can leave the book.
/// Generated pages do not sit inside the Razor layout, so the app navbar is
/// not present. Root-absolute nav hrefs are rewritten after the build in
/// Program.cs (the theme shell is assembled after this plugin runs).
/// </summary>
internal sealed class BackToAppPlugin : IPlugin, IPagePostRenderPlugin
{
    private static readonly byte[] NameBytes = "ywc-back-to-app"u8.ToArray();
    private static readonly byte[] BodyOpen = "<body"u8.ToArray();
    private static readonly byte[] Banner = Encoding.UTF8.GetBytes(
        """
        <div class="ywc-back-bar" style="background:#1a237e;color:#fff;padding:.45rem 1rem;font-family:Roboto,Helvetica,Arial,sans-serif;font-size:.9rem;">
          <a href="/" style="color:#fff;text-decoration:none;font-weight:500;">← Back to Yaesu Web Control</a>
        </div>
        """);

    public ReadOnlySpan<byte> Name => NameBytes;

    public PluginPriority PostRenderPriority => new(PluginBand.Latest, 0);

    public bool NeedsRewrite(ReadOnlySpan<byte> html) =>
        html.IndexOf(BodyOpen) >= 0;

    public void PostRender(in PagePostRenderContext context)
    {
        var html = context.Html;
        var bodyAt = html.IndexOf(BodyOpen);
        if (bodyAt < 0)
        {
            context.Output.Write(html);
            return;
        }

        var gt = html[bodyAt..].IndexOf((byte)'>');
        if (gt < 0)
        {
            context.Output.Write(html);
            return;
        }

        var insertAt = bodyAt + gt + 1;
        context.Output.Write(html[..insertAt]);
        context.Output.Write(Banner);
        context.Output.Write(html[insertAt..]);
    }
}
