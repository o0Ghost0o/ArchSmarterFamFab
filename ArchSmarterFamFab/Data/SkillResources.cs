using System.Text;

namespace ArchSmarterFamFab.Data
{
    internal static class SkillResources
    {
        public static string GetSkillPrompt()
        {
            return ReadEmbeddedResource("ArchSmarterFamFab.EmbeddedContent.SKILL.md");
        }

        public static string GetFamilySchema()
        {
            return ReadEmbeddedResource("ArchSmarterFamFab.EmbeddedContent.family-schema.json");
        }

        public static string GetViewerHtml()
        {
            return ReadEmbeddedResource("ArchSmarterFamFab.EmbeddedContent.revit-family-viewer.html");
        }

        private static string ReadEmbeddedResource(string resourceName)
        {
            var assembly = Assembly.GetExecutingAssembly();
            using (Stream stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                {
                    Debug.WriteLine($"Embedded resource not found: {resourceName}");
                    Debug.WriteLine("Available resources: " + string.Join(", ", assembly.GetManifestResourceNames()));
                    return "";
                }
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                {
                    return reader.ReadToEnd();
                }
            }
        }
    }
}
