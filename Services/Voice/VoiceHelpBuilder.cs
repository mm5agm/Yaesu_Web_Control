using Yaesu_Web_Control.Models;

namespace Yaesu_Web_Control.Services.Voice
{
    /// <summary>One entry in the on-screen voice help: a friendly description of
    /// what the command does plus a few example phrases the user can literally say.</summary>
    public sealed record VoiceHelpItem(string What, IReadOnlyList<string> Examples);

    /// <summary>A titled group of related commands (e.g. "Tuning &amp; bands").</summary>
    public sealed record VoiceHelpGroup(string Name, IReadOnlyList<VoiceHelpItem> Items);

    /// <summary>The whole help document served to the right-click mic-button popup.</summary>
    public sealed record VoiceHelpDoc(string Culture, string Intro, IReadOnlyList<VoiceHelpGroup> Groups);

    /// <summary>
    /// Turns a live <see cref="VoicePhrasesConfig"/> into a display-ready,
    /// grouped help document. Generated from the same config that drives
    /// recognition, so the on-screen command list can never drift from what the
    /// radio actually responds to — including any phrases the user has
    /// customised.
    ///
    /// The layout (which commands, in which groups, in what order) is curated
    /// here; the <em>phrases</em> always come from the config.
    /// </summary>
    public static class VoiceHelpBuilder
    {
        private const int MaxExamples = 5;

        // Friendly, human labels for each intent. Keyed by the same intent
        // names used in VoicePhrasesConfig / IntentDispatcher.
        private static readonly Dictionary<string, string> Labels = new()
        {
            ["SwapVFO"]          = "Swap VFO A and B",
            ["NudgeUp"]          = "Tune up one step",
            ["NudgeDown"]        = "Tune down one step",
            ["BandUp"]           = "Go up one band",
            ["BandDown"]         = "Go down one band",
            ["StatusFrequency"]  = "Ask the current frequency",
            ["StatusMode"]       = "Ask the current mode",
            ["StatusBand"]       = "Ask the current band",
            ["TxOn"]             = "Start transmitting",
            ["TxOff"]            = "Stop transmitting (back to receive)",
            ["SplitOn"]          = "Turn split on",
            ["SplitOff"]         = "Turn split off",
            ["AtuOn"]            = "Turn the antenna tuner on",
            ["AtuOff"]           = "Turn the antenna tuner off",
            ["AtuTune"]          = "Start an antenna auto-tune",
            ["Help"]             = "Speak a short help summary",
            ["NudgeIfWidthUp"]   = "Widen the filter",
            ["NudgeIfWidthDown"] = "Narrow the filter",
            ["SetFrequency"]     = "Tune to a frequency",
            ["SetBand"]          = "Change band — with or without a lead-in word",
            ["SetNudgeStep"]     = "Set the tuning step size",
            ["SetMode"]          = "Set the mode",
            ["SetAfGain"]        = "Set the volume (AF gain)",
            ["SetAttenuator"]    = "Set the attenuator",
            ["SetPreamp"]        = "Set the preamp",
            ["SetAgc"]           = "Set AGC speed",
        };

        public static VoiceHelpDoc Build(VoicePhrasesConfig cfg, string culture)
        {
            var groups = new List<VoiceHelpGroup>();

            void Group(string name, params VoiceHelpItem?[] items)
            {
                var kept = items.Where(i => i != null).Select(i => i!).ToList();
                if (kept.Count > 0) groups.Add(new VoiceHelpGroup(name, kept));
            }

            Group("Tuning & bands",
                Frequency(cfg.SetFrequency),
                Decomposed("SetBand", cfg.SetBand),
                Simple("BandUp", cfg),
                Simple("BandDown", cfg),
                Simple("NudgeUp", cfg),
                Simple("NudgeDown", cfg),
                Simple("SwapVFO", cfg),
                Decomposed("SetNudgeStep", cfg.SetNudgeStep));

            Group("Mode & filters",
                Decomposed("SetMode", cfg.SetMode),
                Simple("NudgeIfWidthUp", cfg),
                Simple("NudgeIfWidthDown", cfg));

            Group("Receiver controls",
                Decomposed("SetAfGain", cfg.SetAfGain),
                Decomposed("SetAttenuator", cfg.SetAttenuator),
                Decomposed("SetPreamp", cfg.SetPreamp),
                Decomposed("SetAgc", cfg.SetAgc));

            Group("Transmit",
                Simple("TxOn", cfg),
                Simple("TxOff", cfg),
                Simple("SplitOn", cfg),
                Simple("SplitOff", cfg),
                Simple("AtuOn", cfg),
                Simple("AtuOff", cfg),
                Simple("AtuTune", cfg));

            Group("Status & help",
                Simple("StatusFrequency", cfg),
                Simple("StatusMode", cfg),
                Simple("StatusBand", cfg),
                Simple("Help", cfg));

            // Custom commands (macros) — grouped by their own Category label.
            foreach (var byCat in (cfg.Macros ?? new())
                         .Where(m => m.Phrases?.Count > 0)
                         .GroupBy(m => string.IsNullOrWhiteSpace(m.Category) ? "Custom commands" : m.Category)
                         .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                var items = byCat
                    .Select(m => new VoiceHelpItem(m.Name, Cap(m.Phrases!)))
                    .ToList();
                if (items.Count > 0) groups.Add(new VoiceHelpGroup(byCat.Key, items));
            }

            const string intro =
                "This mic button is only for giving spoken commands to the app — it does " +
                "not key the PTT and it does not transmit your voice on the air. " +
                "Hold it, say a command, and release when you're done; you don't need to " +
                "pause between words. Bands and step sizes work with or without the lead-in " +
                "word (say \"forty metres\" or \"go to forty metres\").";

            return new VoiceHelpDoc(culture, intro, groups);
        }

        private static VoiceHelpItem? Simple(string key, VoicePhrasesConfig cfg)
        {
            if (cfg.SimpleCommands == null ||
                !cfg.SimpleCommands.TryGetValue(key, out var phrases) ||
                phrases == null || phrases.Count == 0)
                return null;
            return new VoiceHelpItem(Label(key), Cap(phrases));
        }

        private static VoiceHelpItem? Decomposed(string intent, DecomposedCommand? cmd)
        {
            if (cmd == null || cmd.Triggers.Count == 0 || cmd.Vocabulary.Count == 0)
                return null;

            var trigger = cmd.Triggers[0];
            bool optional = VoiceGrammar.TriggerOptionalCommands.Contains(intent);

            // Each vocabulary entry's first spoken form is the representative word.
            var words = cmd.Vocabulary.Values
                .Where(w => w != null && w.Count > 0)
                .Select(w => w[0])
                .ToList();
            if (words.Count == 0) return null;

            var examples = new List<string>();
            int shownWords = 0;
            // For trigger-optional commands, lead with a bare example to teach
            // that the lead-in word can be dropped; then show a few of the
            // "trigger + value" long forms.
            if (optional) { examples.Add(words[0]); shownWords = 1; }

            foreach (var w in optional ? words.Skip(1) : words)
            {
                if (examples.Count >= MaxExamples) break;
                examples.Add($"{trigger} {w}");
                shownWords++;
            }

            return new VoiceHelpItem(Label(intent), Ellipsize(examples, shownWords < words.Count));
        }

        private static VoiceHelpItem? Frequency(SetFrequencyPhrases? cfg)
        {
            if (cfg == null || cfg.Triggers.Count == 0 || cfg.Mhz.Count == 0) return null;

            var trigger = cfg.Triggers[0];
            var examples = new List<string>();

            // A whole-MHz example, then a fractional one, using real config words
            // so a translated pack shows its own vocabulary.
            string? WholeWord(string preferKey) =>
                cfg.Mhz.TryGetValue(preferKey, out var w) && w.Count > 0 ? w[0]
                : cfg.Mhz.Values.FirstOrDefault(v => v.Count > 0)?[0];

            var mhz = WholeWord("7");
            if (mhz != null) examples.Add($"{trigger} {mhz}");

            var mhz2 = WholeWord("14");
            var point = cfg.Point.FirstOrDefault();
            var digit = cfg.FracDigits.ElementAtOrDefault(1); // "one"
            if (mhz2 != null && point != null && digit != null)
                examples.Add($"{trigger} {mhz2} {point} {digit}");

            if (examples.Count == 0) return null;
            return new VoiceHelpItem(Label("SetFrequency"), examples);
        }

        private static string Label(string key) => Labels.TryGetValue(key, out var l) ? l : key;

        private static IReadOnlyList<string> Cap(IReadOnlyList<string> phrases)
        {
            var kept = phrases.Where(p => !string.IsNullOrWhiteSpace(p)).Take(MaxExamples).ToList();
            return Ellipsize(kept, phrases.Count > kept.Count);
        }

        private static IReadOnlyList<string> Ellipsize(List<string> examples, bool more)
        {
            if (more) examples.Add("…");
            return examples;
        }
    }
}
