namespace ArchSmarterFamFab.Data
{
    /// <summary>
    /// Provider-agnostic prompt fragments shared by every <see cref="IFamilyModelClient"/>.
    /// Keeping these in one place guarantees Claude, Gemini, and Kimi are asked for the
    /// exact same schema-v0.1 JSON in the exact same way.
    /// </summary>
    internal static class LlmPrompts
    {
        public const string GenerateUserText =
            "Analyze this image and generate a Revit family JSON definition for this object. " +
            "Use Standard detail level. Propose reasonable default dimensions based on the object type. " +
            "Do not ask questions — generate the JSON directly.";

        public static string SystemPrompt(string skillPrompt, string schemaJson) =>
            skillPrompt +
            "\n\n## JSON Schema\n\nThe generated JSON must conform to this schema:\n\n```json\n" + schemaJson +
            "\n```\n\n## Output Format\n\nReturn ONLY the raw JSON object. Do not wrap it in markdown code fences. " +
            "Do not include any explanation before or after the JSON. The response must start with `{` and end with `}`.";

        public static string RefineUserText(string currentJson, string userInstruction) =>
            "Here is the current Revit family JSON definition:\n\n```json\n" + currentJson + "\n```\n\n" +
            "Modify this family definition according to the following instruction. Return the complete updated JSON:\n\n" +
            userInstruction;
    }

    /// <summary>
    /// Normalizes a model's text response down to the bare JSON object: strips markdown
    /// code fences and any prose before the first `{` / after the last `}`.
    /// </summary>
    internal static class LlmJson
    {
        public static string CleanJsonResponse(string text)
        {
            text = text.Trim();

            if (text.StartsWith("```json"))
                text = text.Substring(7);
            else if (text.StartsWith("```"))
                text = text.Substring(3);

            if (text.EndsWith("```"))
                text = text.Substring(0, text.Length - 3);

            text = text.Trim();

            int firstBrace = text.IndexOf('{');
            int lastBrace = text.LastIndexOf('}');
            if (firstBrace >= 0 && lastBrace > firstBrace)
                text = text.Substring(firstBrace, lastBrace - firstBrace + 1);

            return text;
        }
    }
}
