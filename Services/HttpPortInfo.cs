namespace Yaesu_Web_Control.Services
{
    /// <summary>
    /// Single source of truth for the HTTP (and optional HTTPS) ports YWC is
    /// actually listening on. Resolved once at startup in <c>Program.cs</c>.
    /// </summary>
    public sealed class HttpPortInfo
    {
        public int Port { get; }
        public int? HttpsPort { get; }
        public bool HttpsActive { get; }

        /// <summary>Preferred local URL — HTTPS when active, else HTTP.</summary>
        public string RootUrl => HttpsActive && HttpsPort is int hp
            ? $"https://localhost:{hp}"
            : $"http://localhost:{Port}";

        public string HttpRootUrl => $"http://localhost:{Port}";
        public string? HttpsRootUrl => HttpsActive && HttpsPort is int hp
            ? $"https://localhost:{hp}"
            : null;

        public HttpPortInfo(int port, int? httpsPort = null, bool httpsActive = false)
        {
            Port = port;
            HttpsPort = httpsPort;
            HttpsActive = httpsActive;
        }
    }
}
