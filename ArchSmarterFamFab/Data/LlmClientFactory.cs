namespace ArchSmarterFamFab.Data
{
    /// <summary>
    /// Creates the concrete <see cref="IFamilyModelClient"/> for the selected provider.
    /// All providers share the same embedded skill prompt, JSON schema, and family
    /// contract — only the HTTP wire format differs.
    /// </summary>
    public static class LlmClientFactory
    {
        public static IFamilyModelClient Create(string provider, string apiKey, string modelName)
        {
            return LlmProviders.Normalize(provider) switch
            {
                LlmProviders.Google => new GeminiClient(apiKey, modelName),
                LlmProviders.Moonshot => new KimiClient(apiKey, modelName),
                _ => new ClaudeClient(apiKey, modelName)
            };
        }
    }
}
