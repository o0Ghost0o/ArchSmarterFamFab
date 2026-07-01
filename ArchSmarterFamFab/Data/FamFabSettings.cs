namespace ArchSmarterFamFab.Data
{
    public class FamFabSettings
    {
        /// <summary>Active model provider. One of the <see cref="LlmProviders"/> constants.</summary>
        public string Provider { get; set; } = LlmProviders.Anthropic;

        // BYOK: one plaintext API key per provider.
        public string ClaudeApiKey { get; set; } = "";
        public string GeminiApiKey { get; set; } = "";
        public string MoonshotApiKey { get; set; } = "";

        // Selected model per provider.
        public string ClaudeModel { get; set; } = "claude-opus-4-8";
        public string GeminiModel { get; set; } = "gemini-3.1-pro-preview";
        public string MoonshotModel { get; set; } = "kimi-k2.7-code";

        // Available models offered in the pickers, per provider.
        public List<string> ClaudeModels { get; set; } = new List<string>
        {
            "claude-haiku-4-5-20251001",
            "claude-sonnet-4-6",
            "claude-opus-4-6",
            "claude-opus-4-7",
            "claude-opus-4-8"
        };

        public List<string> GeminiModels { get; set; } = new List<string>
        {
            "gemini-3.1-pro-preview",
            "gemini-3.1-pro",
            "gemini-2.5-pro",
            "gemini-2.5-flash"
        };

        public List<string> MoonshotModels { get; set; } = new List<string>
        {
            "kimi-for-coding",
            "kimi-k2.7-code",
            "kimi-k2.6",
            "kimi-latest",
            "moonshot-v1-8k-vision-preview",
            "moonshot-v1-32k-vision-preview",
            "moonshot-v1-128k-vision-preview"
        };
    }
}
