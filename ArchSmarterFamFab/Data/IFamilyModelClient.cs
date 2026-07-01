using System.Collections.Generic;
using System.Threading.Tasks;

namespace ArchSmarterFamFab.Data
{
    /// <summary>
    /// A vision-capable model client that turns one or more reference images (plus the
    /// embedded skill prompt and JSON schema) into a Revit family JSON definition, and can
    /// refine an existing definition from a follow-up instruction. Implemented per provider
    /// (Anthropic Claude, Google Gemini, Moonshot Kimi).
    /// </summary>
    public interface IFamilyModelClient
    {
        Task<string> GenerateFamilyFromImagesAsync(IReadOnlyList<ImageInput> images,
            string skillPrompt, string schemaJson, string userContext = null);

        Task<string> RefineFamilyAsync(string currentJson, string userInstruction,
            string skillPrompt, string schemaJson, IReadOnlyList<ImageInput> images = null);
    }
}
