namespace ArchSmarterFamFab.Data
{
    public class FamFabSettings
    {
        public string ClaudeApiKey { get; set; } = "";
        public string ModelName { get; set; } = "claude-opus-4-8";
        public List<string> AvailableModels { get; set; } = new List<string>
        {
            "claude-haiku-4-5-20251001",
            "claude-sonnet-4-6",
            "claude-opus-4-6",
            "claude-opus-4-7",
            "claude-opus-4-8"
        };
    }
}
