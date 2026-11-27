using System.Net.Http.Json;
using System.Text.Json;

namespace Yaesu_Web_Control.Services.RadioMenu;

/// <summary>
/// The tray's Exit asks YWC itself, over localhost, whether a front-panel level
/// changed since the last save of the radio's settings, and saves on request:
/// the same endpoints the Close YWC page uses, so both ask the same way.
/// Any failure means "don't ask": closing YWC must never be blocked by this.
/// </summary>
public static class TrayExitSave
{
    private static HttpClient NewClient(TimeSpan timeout) => new(new HttpClientHandler
    {
        // YWC's own certificate on localhost, which may be self-signed.
        ServerCertificateCustomValidationCallback = (req, _, _, _) => req.RequestUri?.IsLoopback == true,
    })
    { Timeout = timeout };

    /// <summary>The message to show if YWC should ask to save before closing; null if not.</summary>
    public static string? Check(string rootUrl)
    {
        try
        {
            using var http = NewClient(TimeSpan.FromSeconds(8));
            // Off the UI thread, so no synchronisation context is captured.
            var json = Task.Run(() => http.GetFromJsonAsync<JsonElement>(rootUrl.TrimEnd('/') + "/api/radiomenu/exit-check"))
                .GetAwaiter().GetResult();
            if (json.TryGetProperty("ask", out var ask) && ask.ValueKind == JsonValueKind.True
                && json.TryGetProperty("message", out var msg) && msg.GetString() is { Length: > 0 } text)
                return text;
        }
        catch
        {
            // No answer: close as before.
        }
        return null;
    }

    /// <summary>Saves the radio menu. Null when saved, otherwise why it failed.</summary>
    public static string? Save(string rootUrl)
    {
        try
        {
            using var http = NewClient(TimeSpan.FromSeconds(90));
            var r = Task.Run(() => http.PostAsync(rootUrl.TrimEnd('/') + "/api/radiomenu/save", null)).GetAwaiter().GetResult();
            if (r.IsSuccessStatusCode) return null;
            var body = Task.Run(() => r.Content.ReadAsStringAsync()).GetAwaiter().GetResult();
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out var e) && e.GetString() is { Length: > 0 } err)
                    return err;
            }
            catch (JsonException) { }
            return $"YWC answered {(int)r.StatusCode}.";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
