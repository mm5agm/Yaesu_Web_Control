namespace Yaesu_Web_Control.Models.Alexa;

// Minimal shape of an Alexa Skill request as POSTed to our webhook.
// The real Amazon JSON is much larger (session, context, user, device etc.)
// but we only need these fields for first-pass intent handling.
// Reference: https://developer.amazon.com/en-US/docs/alexa/custom-skills/request-and-response-json-reference.html

public class AlexaRequest
{
    public string Version { get; set; } = "";
    public AlexaRequestBody Request { get; set; } = new();
}

public class AlexaRequestBody
{
    /// <summary>One of "LaunchRequest", "IntentRequest", "SessionEndedRequest".</summary>
    public string Type { get; set; } = "";
    public string RequestId { get; set; } = "";
    /// <summary>Amazon includes a UTC timestamp. We check it in signature verification
    /// to reject replays older than ~150 seconds.</summary>
    public DateTime Timestamp { get; set; }
    public string Locale { get; set; } = "";
    public AlexaIntent? Intent { get; set; }
}

public class AlexaIntent
{
    public string Name { get; set; } = "";
    public Dictionary<string, AlexaSlot> Slots { get; set; } = new();
}

public class AlexaSlot
{
    public string Name { get; set; } = "";
    public string? Value { get; set; }
}
