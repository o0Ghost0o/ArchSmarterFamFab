namespace ArchSmarterFamFab.Data
{
    /// <summary>
    /// Canonical provider identifiers (stored in settings) and their display metadata.
    /// The stored value is always one of the constants below; display strings are only
    /// for the UI.
    /// </summary>
    public static class LlmProviders
    {
        public const string Anthropic = "Anthropic";
        public const string Google = "Google";
        public const string Moonshot = "Moonshot";

        public static readonly IReadOnlyList<string> All = new[] { Anthropic, Google, Moonshot };

        /// <summary>Coerce an arbitrary/legacy value to a known provider (defaults to Anthropic).</summary>
        public static string Normalize(string provider) =>
            provider == Google || provider == Moonshot ? provider : Anthropic;

        public static string DisplayName(string provider) => Normalize(provider) switch
        {
            Google => "Google (Gemini)",
            Moonshot => "Moonshot (Kimi)",
            _ => "Anthropic (Claude)"
        };

        public static string FromDisplay(string display) => display switch
        {
            "Google (Gemini)" => Google,
            "Moonshot (Kimi)" => Moonshot,
            _ => Anthropic
        };

        public static string KeyLabel(string provider) => DisplayName(provider) + " API Key";

        public static string KeyHint(string provider) => Normalize(provider) switch
        {
            Google => "Get a key from Google AI Studio.",
            Moonshot => "Moonshot key (platform.moonshot.ai) or Kimi Code key (kimi.com) — both auto-detected.",
            _ => "Key usually starts with sk-ant-."
        };
    }
}
