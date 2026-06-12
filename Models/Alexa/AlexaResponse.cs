namespace Yaesu_Web_Control.Models.Alexa;

// Minimal shape of an Alexa Skill response. The real spec has more (cards,
// directives, session attributes); for first pass we only need plain spoken
// text and the end-session flag.

public class AlexaResponse
{
    public string Version { get; set; } = "1.0";
    public AlexaResponseBody Response { get; set; } = new();

    /// <summary>Build a simple "say this text" response. endSession=true is
    /// the typical case — Alexa speaks and the session terminates.</summary>
    public static AlexaResponse Speak(string text, bool endSession = true) => new()
    {
        Response = new()
        {
            OutputSpeech = new() { Type = "PlainText", Text = text },
            ShouldEndSession = endSession
        }
    };
}

public class AlexaResponseBody
{
    public AlexaOutputSpeech OutputSpeech { get; set; } = new();
    public bool ShouldEndSession { get; set; } = true;
}

public class AlexaOutputSpeech
{
    public string Type { get; set; } = "PlainText";
    public string Text { get; set; } = "";
}
