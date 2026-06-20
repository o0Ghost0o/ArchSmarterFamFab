using System.Windows.Interop;
using ArchSmarterFamFab.Data;
using ArchSmarterFamFab.UI;

namespace ArchSmarterFamFab
{
    [Transaction(TransactionMode.Manual)]
    public class CmdFamFab : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc.Document;

            if (!doc.IsFamilyDocument)
            {
                TaskDialog.Show("FamFab",
                    "FamFab must be run inside the Family Editor.\n\nOpen or create a family document first.");
                return Result.Failed;
            }

            var settingsManager = new FamFabSettingsManager();
            string apiKey = settingsManager.GetClaudeApiKey();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                TaskDialog.Show("FamFab",
                    "Please configure your Claude API key first.\n\nClick the FamFab Settings button to enter your key.");
                return Result.Failed;
            }

            var generateWindow = new GenerateWindow(apiKey, settingsManager);
            new WindowInteropHelper(generateWindow).Owner = uiapp.MainWindowHandle;

            if (generateWindow.ShowDialog() != true)
                return Result.Cancelled;

            string familyJson = generateWindow.FamilyJson;
            string familyName = generateWindow.FamilyName;
            SaveJson(familyJson, "api-response", familyName);
            string sourceImagePath = SaveSourceImage(generateWindow.SourceImageBytes, generateWindow.SourceImageMimeType, familyName);

            var previewWindow = new PreviewWindow(familyJson, apiKey, settingsManager.GetModelName(),
                generateWindow.SourceImageBytes, generateWindow.SourceImageMimeType, sourceImagePath, familyName);
            new WindowInteropHelper(previewWindow).Owner = uiapp.MainWindowHandle;

            if (previewWindow.ShowDialog() == true && previewWindow.GenerateRequested)
            {
                familyName = previewWindow.FamilyName;
                string finalJson = InjectFamilyName(previewWindow.FinalFamilyJson, familyName);
                SaveJson(finalJson, "final", familyName);

                var generator = new FamilyGenerator();
                Autodesk.Revit.ApplicationServices.Application app = uiapp.Application;
                GenerationResult genResult = generator.Execute(doc, app, finalJson);

                if (genResult.Success)
                {
                    string summary = $"Family created successfully!\n\n" +
                        $"Parameters: {genResult.ParameterCount}\n" +
                        $"Reference Planes: {genResult.RefPlaneCount}\n" +
                        $"Geometry Elements: {genResult.GeometryCount}";

                    if (genResult.ErrorCount > 0)
                        summary += $"\nWarnings: {genResult.ErrorCount}\n" +
                            string.Join("\n", genResult.Errors);

                    TaskDialog.Show("FamFab - Complete", summary);
                }
                else
                {
                    string errorMsg = genResult.ErrorMessage ?? "Unknown error during generation.";
                    if (genResult.Errors.Count > 0)
                        errorMsg += "\n\nDetails:\n" + string.Join("\n", genResult.Errors);

                    TaskDialog.Show("FamFab - Error", errorMsg);
                    return Result.Failed;
                }
            }

            return Result.Succeeded;
        }

        private static string SaveSourceImage(byte[] imageBytes, string mimeType, string familyName = null)
        {
            if (imageBytes == null) return null;
            try
            {
                string folder = FamFabSettingsManager.GetLogsFolderPath();
                Directory.CreateDirectory(folder);

                string ext = mimeType switch
                {
                    "image/png" => ".png",
                    "image/webp" => ".webp",
                    _ => ".jpg"
                };
                string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                string slug = SanitizeFileName(familyName);
                string baseName = string.IsNullOrEmpty(slug)
                    ? $"{timestamp}-source"
                    : $"{timestamp}-{slug}-source";
                string path = Path.Combine(folder, $"{baseName}{ext}");
                File.WriteAllBytes(path, imageBytes);
                return path;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to save source image: {ex.Message}");
                return null;
            }
        }

        private static void SaveJson(string json, string label, string familyName = null)
        {
            try
            {
                string folder = FamFabSettingsManager.GetLogsFolderPath();
                Directory.CreateDirectory(folder);

                string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                string slug = SanitizeFileName(familyName);
                string fileName = string.IsNullOrEmpty(slug)
                    ? $"{timestamp}-{label}.json"
                    : $"{timestamp}-{slug}-{label}.json";
                File.WriteAllText(Path.Combine(folder, fileName), json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to save JSON log: {ex.Message}");
            }
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            char[] invalid = Path.GetInvalidFileNameChars();
            string sanitized = new string(name
                .Where(c => !invalid.Contains(c))
                .ToArray())
                .Trim()
                .Replace(' ', '-')
                .ToLowerInvariant();
            while (sanitized.Contains("--"))
                sanitized = sanitized.Replace("--", "-");
            return sanitized.Trim('-');
        }

        private static string InjectFamilyName(string json, string familyName)
        {
            if (string.IsNullOrWhiteSpace(familyName)) return json;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;
                using var ms = new System.IO.MemoryStream();
                using (var writer = new System.Text.Json.Utf8JsonWriter(ms, new System.Text.Json.JsonWriterOptions { Indented = true }))
                {
                    writer.WriteStartObject();
                    foreach (var prop in root.EnumerateObject())
                    {
                        if (prop.Name == "metadata")
                        {
                            writer.WritePropertyName("metadata");
                            writer.WriteStartObject();
                            writer.WriteString("name", familyName);
                            foreach (var metaProp in prop.Value.EnumerateObject())
                            {
                                if (metaProp.Name != "name")
                                    metaProp.WriteTo(writer);
                            }
                            writer.WriteEndObject();
                        }
                        else
                        {
                            prop.WriteTo(writer);
                        }
                    }
                    writer.WriteEndObject();
                }
                return System.Text.Encoding.UTF8.GetString(ms.ToArray());
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to inject family name: {ex.Message}");
                return json;
            }
        }

        internal static PushButtonData GetButtonData()
        {
            string buttonInternalName = "btnFamFab";
            string buttonTitle = "Fabricate";

            Helpers.ButtonDataClass myButtonData = new Helpers.ButtonDataClass(
                buttonInternalName,
                buttonTitle,
                MethodBase.GetCurrentMethod().DeclaringType?.FullName,
                Properties.Resources.fabricate_cube_plus_32,
                Properties.Resources.fabricate_cube_plus_16,
                "Generate a Revit family from an image using AI");

            return myButtonData.Data;
        }
    }
}
