using System.Speech.Recognition;
using System.Xml.Linq;
using Yaesu_Web_Control.Models;

namespace Yaesu_Web_Control.Services.Voice
{
    /// <summary>
    /// Builds a SAPI grammar from a <see cref="VoicePhrasesConfig"/>. All
    /// phrase lists are user-configurable; this class only knows about
    /// structure (which semantic keys exist, how parameters compose), not
    /// specific words. That means a German user can replace "set mode" with
    /// "Betriebsart" and "eighty metres" with "achtzig Meter" without any
    /// code changes.
    ///
    /// Grammar patterns used:
    ///
    ///   SimpleCommands   — flat Choices of complete phrases; each phrase maps
    ///                      directly to an intent string via SemanticResultValue.
    ///
    ///   SetMode/SetBand  — DecomposedCommand: a non-semantic trigger prefix
    ///                      ("mode", "set mode") followed by a value Choices
    ///                      ("u s b" → "SetMode:USB"). The trigger is consumed
    ///                      but carries no semantic output; the value word
    ///                      encodes the full "IntentPrefix:Value" in one key.
    ///
    ///   Macros           — flat Choices; each phrase maps to
    ///                      "Macro:{name}|{cat}" encoding both spoken name
    ///                      (for the TTS confirmation) and CAT string to send.
    ///
    ///   SetFrequency     — three flat variants (whole / one frac digit /
    ///                      three frac digits) to avoid the SAPI nested-
    ///                      optional compile bug ("'' rule reference not
    ///                      defined"). Uses GrammarBuilder + Choices throughout
    ///                      — Grammar.LoadCfg throws PlatformNotSupportedException
    ///                      on .NET 6+ so SRGS XML loading is not available.
    /// </summary>
    internal static class VoiceGrammar
    {
        /// <summary>
        /// Decomposed commands whose trigger prefix is made optional in the
        /// grammar (see BuildDecomposed). Their vocabulary carries its own unit
        /// word ("...metres", "...hertz"/"...k"), so a bare value is unambiguous
        /// and the user can skip the lead-in ("forty metres" == "go to forty
        /// metres"). Single source of truth: consumed by Build() below and by
        /// VoiceHelpBuilder so the on-screen help matches what actually works.
        /// The other decomposed commands (mode/att/preamp/agc/af gain) have bare
        /// vocabulary ("data", "fifty", "fast") that would collide across
        /// branches without a mandatory trigger, so they stay trigger-required.
        /// </summary>
        public static readonly IReadOnlySet<string> TriggerOptionalCommands =
            new HashSet<string>(StringComparer.Ordinal) { "SetBand", "SetNudgeStep" };

        public static Grammar Build(VoicePhrasesConfig cfg, string culture = "en-GB")
        {
            // GrammarBuilder.Culture defaults to the current thread's culture,
            // which on a non-English Windows install would silently mismatch
            // the SpeechRecognitionEngine's constructed culture and throw on
            // LoadGrammar -- explicit is required once more than one locale
            // is in play (§4).
            var cultureInfo = new System.Globalization.CultureInfo(culture);
            var variants = new List<GrammarBuilder>();

            void Try(string name, Func<GrammarBuilder?> make)
            {
                try
                {
                    var gb = make();
                    if (gb == null) return;
                    gb.Culture = cultureInfo;
                    _ = new Grammar(gb);   // compile-check before adding
                    variants.Add(gb);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"[VoiceGrammar] Variant '{name}' failed to compile: {ex.Message}", ex);
                }
            }

            // Simple commands — each is a flat Choices of complete trigger phrases.
            foreach (var (key, cmd) in cfg.SimpleCommands)
            {
                var k = key;
                Try(k, () => BuildSimple(k, cmd));
            }

            Try("SetMode",                () => BuildDecomposed("SetMode",        cfg.SetMode));
            // SetBand/SetNudgeStep vocabulary words carry their own unit
            // ("...metres", "...hertz"), so they're self-identifying and the
            // trigger can be optional — the user can say a bare "forty metres"
            // as well as "go to forty metres".
            Try("SetBand",                () => BuildDecomposed("SetBand",        cfg.SetBand,      triggerOptional: TriggerOptionalCommands.Contains("SetBand")));
            Try("SetNudgeStep",           () => BuildDecomposed("SetNudgeStep",   cfg.SetNudgeStep, triggerOptional: TriggerOptionalCommands.Contains("SetNudgeStep")));
            Try("SetAttenuator",          () => BuildDecomposed("SetAttenuator",  cfg.SetAttenuator));
            Try("SetPreamp",              () => BuildDecomposed("SetPreamp",      cfg.SetPreamp));
            Try("SetAgc",                 () => BuildDecomposed("SetAgc",         cfg.SetAgc));
            Try("SetAfGain",              () => BuildDecomposed("SetAfGain",      cfg.SetAfGain));
            if (cfg.Macros?.Count > 0)
                Try("Macros",             () => BuildMacros(cfg.Macros));
            Try("SetFrequency_Whole",     () => BuildSetFrequency_Whole(cfg.SetFrequency));
            Try("SetFrequency_OneDigit",  () => BuildSetFrequency_OneDigit(cfg.SetFrequency));
            Try("SetFrequency_ThreeDigit",() => BuildSetFrequency_ThreeDigit(cfg.SetFrequency));

            if (variants.Count == 0)
                throw new InvalidOperationException("[VoiceGrammar] No grammar variants compiled — check phrase lists.");

            var root = new Choices(variants.ToArray());
            var rootBuilder = new GrammarBuilder(root) { Culture = cultureInfo };
            return new Grammar(rootBuilder) { Name = "YwcCommands" };
        }

        // ── SRGS/SISR export ────────────────────────────────────────────
        // Walks the same VoicePhrasesConfig structure as Build() above, but
        // emits a W3C SRGS 1.0 document (with SISR <tag> output matching the
        // "Intent" / "Intent:Value" / "Macro:{name}|{cat}" encoding used by
        // the live grammar and documented in Grammars/README.md) instead of
        // a GrammarBuilder. This file is never loaded back into the live
        // engine (see class remarks and docs/VoiceControl/language-pack-manager-design.md
        // §1.2/§5.2) — it exists purely as a portable, human/tool-readable
        // reference copy produced on every save/export so it can never drift
        // from the JSON that actually drives recognition.

        private static readonly XNamespace SrgsNs = "http://www.w3.org/2001/06/grammar";

        public static string GenerateSrgs(VoicePhrasesConfig cfg, string culture)
        {
            var commandItems = new List<XElement>();

            foreach (var (key, phrases) in cfg.SimpleCommands)
            {
                var item = BuildSimpleSrgsItem(key, phrases);
                if (item != null) commandItems.Add(item);
            }

            AddDecomposedSrgsItem(commandItems, "SetMode", cfg.SetMode);
            AddDecomposedSrgsItem(commandItems, "SetBand", cfg.SetBand);
            AddDecomposedSrgsItem(commandItems, "SetNudgeStep", cfg.SetNudgeStep);
            AddDecomposedSrgsItem(commandItems, "SetAttenuator", cfg.SetAttenuator);
            AddDecomposedSrgsItem(commandItems, "SetPreamp", cfg.SetPreamp);
            AddDecomposedSrgsItem(commandItems, "SetAgc", cfg.SetAgc);
            AddDecomposedSrgsItem(commandItems, "SetAfGain", cfg.SetAfGain);

            foreach (var macro in cfg.Macros ?? new())
            {
                var item = BuildMacroSrgsItem(macro);
                if (item != null) commandItems.Add(item);
            }

            commandItems.AddRange(BuildSetFrequencySrgsItems(cfg.SetFrequency));

            var commandsRule = new XElement(SrgsNs + "rule",
                new XAttribute("id", "commands"),
                new XAttribute("scope", "public"),
                new XElement(SrgsNs + "one-of", commandItems));

            var grammar = new XElement(SrgsNs + "grammar",
                new XAttribute("version", "1.0"),
                new XAttribute(XNamespace.Xml + "lang", culture),
                new XAttribute("root", "commands"),
                new XAttribute("tag-format", "semantics/1.0"),
                new XComment(" Generated by VoiceGrammar.GenerateSrgs from the matching Commands." + culture +
                             ".json. Reference/portability copy only — the live engine never loads this " +
                             "file back; see Grammars/README.md. "),
                commandsRule);
            grammar.Add(BuildSetFrequencySupportRules(cfg.SetFrequency));

            var doc = new XDocument(new XDeclaration("1.0", "UTF-8", null), grammar);
            using var sw = new Utf8StringWriter();
            doc.Save(sw);
            return sw.ToString();
        }

        // StringWriter.Encoding defaults to UTF-16, which XmlWriter uses as the
        // declared encoding regardless of the XDeclaration passed to Save — but
        // this string is always persisted as UTF-8 bytes afterwards (File.WriteAllText,
        // ZipArchive StreamWriter). Without this override the declaration lies
        // about its own encoding and strict XML parsers reject the file.
        private sealed class Utf8StringWriter : StringWriter
        {
            public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        }

        private static string EscapeTag(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

        private static XElement? BuildSimpleSrgsItem(string intent, IReadOnlyList<string> phrases)
        {
            if (phrases == null || phrases.Count == 0) return null;
            return new XElement(SrgsNs + "item",
                new XElement(SrgsNs + "one-of", phrases.Select(p => new XElement(SrgsNs + "item", p))),
                new XElement(SrgsNs + "tag", $"out=\"{EscapeTag(intent)}\";"));
        }

        private static void AddDecomposedSrgsItem(List<XElement> items, string intentPrefix, DecomposedCommand cmd)
        {
            if (cmd == null || cmd.Triggers.Count == 0 || cmd.Vocabulary.Count == 0) return;

            var valueItems = new List<XElement>();
            foreach (var (key, words) in cmd.Vocabulary)
            {
                if (words == null) continue;
                foreach (var word in words)
                    valueItems.Add(new XElement(SrgsNs + "item", word,
                        new XElement(SrgsNs + "tag", $"out=\"{EscapeTag($"{intentPrefix}:{key}")}\";")));
            }
            if (valueItems.Count == 0) return;

            items.Add(new XElement(SrgsNs + "item",
                new XElement(SrgsNs + "one-of", cmd.Triggers.Select(t => new XElement(SrgsNs + "item", t))),
                new XElement(SrgsNs + "one-of", valueItems)));
        }

        private static XElement? BuildMacroSrgsItem(MacroDefinition macro)
        {
            if (macro.Phrases == null || macro.Phrases.Count == 0 || string.IsNullOrWhiteSpace(macro.Cat)) return null;
            var intent = $"Macro:{macro.Name}|{macro.Cat}";
            return new XElement(SrgsNs + "item",
                new XElement(SrgsNs + "one-of", macro.Phrases.Select(p => new XElement(SrgsNs + "item", p))),
                new XElement(SrgsNs + "tag", $"out=\"{EscapeTag(intent)}\";"));
        }

        // Mirrors BuildSetFrequency_Whole/OneDigit/ThreeDigit above: three
        // flat variants referencing shared sub-rules, rather than nested
        // optionals — SRGS has no SAPI compile bug to dodge, but keeping the
        // same shape means this stays a faithful transcription of the live
        // grammar rather than a from-scratch reinterpretation. Point/frac-
        // digit/megahertz words carry no semantic tag here, matching the
        // live GrammarBuilder, which appends those as plain Choices with no
        // SemanticResultValue — only the trigger and the whole-MHz value
        // carry semantics in either representation.
        private static IEnumerable<XElement> BuildSetFrequencySrgsItems(SetFrequencyPhrases cfg)
        {
            if (cfg == null || cfg.Triggers.Count == 0 || cfg.Mhz.Count == 0) yield break;

            XElement TriggerOneOf() => new(SrgsNs + "one-of",
                cfg.Triggers.Select(t => new XElement(SrgsNs + "item", t,
                    new XElement(SrgsNs + "tag", "out=\"SetFrequency\";"))));
            XElement OptionalMegahertz() => new(SrgsNs + "item", new XAttribute("repeat", "0-1"),
                new XElement(SrgsNs + "ruleref", new XAttribute("uri", "#megahertzSuffix")));

            yield return new XElement(SrgsNs + "item",
                TriggerOneOf(),
                new XElement(SrgsNs + "ruleref", new XAttribute("uri", "#mhzWhole")),
                OptionalMegahertz());

            if (cfg.FracDigits.Count > 0 && cfg.Point.Count > 0)
            {
                yield return new XElement(SrgsNs + "item",
                    TriggerOneOf(),
                    new XElement(SrgsNs + "ruleref", new XAttribute("uri", "#mhzWhole")),
                    new XElement(SrgsNs + "ruleref", new XAttribute("uri", "#point")),
                    new XElement(SrgsNs + "ruleref", new XAttribute("uri", "#fracDigit")),
                    OptionalMegahertz());

                yield return new XElement(SrgsNs + "item",
                    TriggerOneOf(),
                    new XElement(SrgsNs + "ruleref", new XAttribute("uri", "#mhzWhole")),
                    new XElement(SrgsNs + "ruleref", new XAttribute("uri", "#point")),
                    new XElement(SrgsNs + "ruleref", new XAttribute("uri", "#fracDigit")),
                    new XElement(SrgsNs + "ruleref", new XAttribute("uri", "#fracDigit")),
                    new XElement(SrgsNs + "ruleref", new XAttribute("uri", "#fracDigit")),
                    OptionalMegahertz());
            }
        }

        private static IEnumerable<XElement> BuildSetFrequencySupportRules(SetFrequencyPhrases cfg)
        {
            if (cfg == null) yield break;

            if (cfg.Mhz.Count > 0)
            {
                var mhzItems = new List<XElement>();
                foreach (var (mhzKey, words) in cfg.Mhz)
                {
                    if (!int.TryParse(mhzKey, out var mhz) || words == null) continue;
                    foreach (var w in words)
                        mhzItems.Add(new XElement(SrgsNs + "item", w, new XElement(SrgsNs + "tag", $"out={mhz};")));
                }
                if (mhzItems.Count > 0)
                    yield return new XElement(SrgsNs + "rule", new XAttribute("id", "mhzWhole"),
                        new XElement(SrgsNs + "one-of", mhzItems));
            }

            if (cfg.Point.Count > 0)
                yield return new XElement(SrgsNs + "rule", new XAttribute("id", "point"),
                    new XElement(SrgsNs + "one-of", cfg.Point.Select(p => new XElement(SrgsNs + "item", p))));

            if (cfg.FracDigits.Count > 0)
                yield return new XElement(SrgsNs + "rule", new XAttribute("id", "fracDigit"),
                    new XElement(SrgsNs + "one-of", cfg.FracDigits.Select(d => new XElement(SrgsNs + "item", d))));

            if (cfg.Megahertz.Count > 0)
                yield return new XElement(SrgsNs + "rule", new XAttribute("id", "megahertzSuffix"),
                    new XElement(SrgsNs + "one-of", cfg.Megahertz.Select(m => new XElement(SrgsNs + "item", m))));
        }

        // ── Simple commands ───────────────────────────────────────────────

        private static GrammarBuilder? BuildSimple(string intent, IReadOnlyList<string> phrases)
        {
            if (phrases.Count == 0) return null;
            var c = new Choices();
            foreach (var p in phrases)
                c.Add(new SemanticResultValue(p, intent));
            var gb = new GrammarBuilder();
            gb.Append(new SemanticResultKey("intent", c));
            return gb;
        }

        // ── DecomposedCommand: SetMode / SetBand ──────────────────────────
        // User says: [trigger] [vocabulary word]
        //   "set mode"  + "u s b"          → intent = "SetMode:USB"
        //   "go to"     + "eighty metres"  → intent = "SetBand:80"
        //
        // The trigger is a non-semantic prefix (just consumed). The vocabulary
        // word carries the full encoded intent via SemanticResultValue.
        // NormaliseIntent in VoiceControlService splits on ':' to recover
        // the intent name and parameter value.

        private static GrammarBuilder? BuildDecomposed(string intentPrefix, DecomposedCommand cmd, bool triggerOptional = false)
        {
            if (cmd.Triggers.Count == 0 || cmd.Vocabulary.Count == 0) return null;

            var valueChoices = new Choices();
            bool any = false;
            foreach (var (key, words) in cmd.Vocabulary)
            {
                foreach (var word in words)
                {
                    valueChoices.Add(new SemanticResultValue(word, $"{intentPrefix}:{key}"));
                    any = true;
                }
            }
            if (!any) return null;

            var gb = new GrammarBuilder();
            // Non-semantic trigger prefix. When triggerOptional, wrap it in a
            // 0-1 repeat so a bare vocabulary word ("forty metres") matches too —
            // only safe where the vocabulary is self-identifying (see
            // TriggerOptionalCommands).
            if (triggerOptional)
                gb.Append(new GrammarBuilder(new Choices(cmd.Triggers.ToArray())), 0, 1);
            else
                gb.Append(new Choices(cmd.Triggers.ToArray()));      // non-semantic trigger
            gb.Append(new SemanticResultKey("intent", valueChoices)); // value encodes intent
            return gb;
        }

        // ── Macros (user-defined CAT shortcuts) ──────────────────────────
        // Intent encoding: "Macro:{name}|{cat}" — name is for the spoken
        // confirmation, cat is sent verbatim to the radio (split on ';').
        // The '|' separator is safe because CAT strings never contain '|'.

        private static GrammarBuilder? BuildMacros(List<MacroDefinition> macros)
        {
            var c = new Choices();
            bool any = false;
            foreach (var macro in macros)
            {
                if (macro.Phrases == null || macro.Phrases.Count == 0) continue;
                if (string.IsNullOrWhiteSpace(macro.Cat)) continue;
                var intent = $"Macro:{macro.Name}|{macro.Cat}";
                foreach (var phrase in macro.Phrases)
                    c.Add(new SemanticResultValue(phrase, intent));
                any = true;
            }
            if (!any) return null;
            var gb = new GrammarBuilder();
            gb.Append(new SemanticResultKey("intent", c));
            return gb;
        }

        // ── SetFrequency variants ─────────────────────────────────────────
        // Three flat variants to avoid the nested-optional SAPI compile bug.
        // Triggers now include the connector word ("tune to", "set frequency to")
        // so there is no separate Connectors list — the user edits one field.

        private static GrammarBuilder? BuildSetFrequency_Whole(SetFrequencyPhrases cfg)
        {
            if (!HasFrequencyBase(cfg)) return null;
            var gb = new GrammarBuilder();
            gb.Append(new SemanticResultKey("intent", FrequencyTriggerChoices(cfg)));
            gb.Append(new SemanticResultKey("mhz_whole", MhzChoices(cfg)));
            gb.Append(new GrammarBuilder(new Choices(cfg.Megahertz.ToArray())), 0, 1);
            return gb;
        }

        private static GrammarBuilder? BuildSetFrequency_OneDigit(SetFrequencyPhrases cfg)
        {
            if (!HasFrequencyBase(cfg) || cfg.FracDigits.Count == 0 || cfg.Point.Count == 0) return null;
            var gb = new GrammarBuilder();
            gb.Append(new SemanticResultKey("intent", FrequencyTriggerChoices(cfg)));
            gb.Append(new SemanticResultKey("mhz_whole", MhzChoices(cfg)));
            gb.Append(new Choices(cfg.Point.ToArray()));
            gb.Append(new Choices(cfg.FracDigits.ToArray()));
            gb.Append(new GrammarBuilder(new Choices(cfg.Megahertz.ToArray())), 0, 1);
            return gb;
        }

        private static GrammarBuilder? BuildSetFrequency_ThreeDigit(SetFrequencyPhrases cfg)
        {
            if (!HasFrequencyBase(cfg) || cfg.FracDigits.Count == 0 || cfg.Point.Count == 0) return null;
            var gb = new GrammarBuilder();
            gb.Append(new SemanticResultKey("intent", FrequencyTriggerChoices(cfg)));
            gb.Append(new SemanticResultKey("mhz_whole", MhzChoices(cfg)));
            gb.Append(new Choices(cfg.Point.ToArray()));
            // Three separate Choices instances — reusing one triggers SAPI compile bug.
            gb.Append(new Choices(cfg.FracDigits.ToArray()));
            gb.Append(new Choices(cfg.FracDigits.ToArray()));
            gb.Append(new Choices(cfg.FracDigits.ToArray()));
            gb.Append(new GrammarBuilder(new Choices(cfg.Megahertz.ToArray())), 0, 1);
            return gb;
        }

        private static bool HasFrequencyBase(SetFrequencyPhrases cfg) =>
            cfg.Triggers.Count > 0 && cfg.Mhz.Count > 0;

        private static Choices FrequencyTriggerChoices(SetFrequencyPhrases cfg)
        {
            var c = new Choices();
            foreach (var t in cfg.Triggers)
                c.Add(new SemanticResultValue(t, "SetFrequency"));
            return c;
        }

        private static Choices MhzChoices(SetFrequencyPhrases cfg)
        {
            var c = new Choices();
            foreach (var (mhzKey, words) in cfg.Mhz)
            {
                if (!int.TryParse(mhzKey, out var mhz)) continue;
                foreach (var w in words)
                    c.Add(new SemanticResultValue(w, mhz));
            }
            return c;
        }
    }
}
