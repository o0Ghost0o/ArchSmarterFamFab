using System.Windows.Interop;
using ArchSmarterFamFab.Data;
using ArchSmarterFamFab.UI;

namespace ArchSmarterFamFab
{
    [Transaction(TransactionMode.Manual)]
    public class CmdFamFab : IExternalCommand
    {
        private static bool _tripoSRDependenciesChecked;

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
            string apiKey = settingsManager.GetApiKey();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                string providerName = Data.LlmProviders.DisplayName(settingsManager.GetProvider());
                TaskDialog.Show("FamFab",
                    $"Please configure your {providerName} API key first.\n\nClick the FamFab Settings button to enter your key.");
                return Result.Failed;
            }

            if (!_tripoSRDependenciesChecked)
            {
                var env = TripoSRDependencyInstaller.FindEnvironment();
                if (!TripoSRDependencyInstaller.IsInstalled(env.PythonExe))
                {
                    if (!env.IsUvManaged && string.IsNullOrEmpty(env.PythonExe))
                    {
                        TaskDialog.Show("FamFab - TripoSR",
                            "TripoSR requires Python 3.11+ or the uv package manager, but neither was found on this machine.\n\n" +
                            "Please install uv (https://docs.astral.sh/uv/) or Python 3.11+, then restart Revit.");
                        _tripoSRDependenciesChecked = true;
                        return Result.Failed;
                    }

                    var installer = new InstallerWindow(env);
                    new WindowInteropHelper(installer).Owner = uiapp.MainWindowHandle;
                    if (installer.ShowDialog() != true)
                        return Result.Cancelled;
                }
                _tripoSRDependenciesChecked = true;
            }

            var generateWindow = new GenerateWindow(apiKey, settingsManager);
            new WindowInteropHelper(generateWindow).Owner = uiapp.MainWindowHandle;

            if (generateWindow.ShowDialog() != true)
                return Result.Cancelled;

            string familyJson = generateWindow.FamilyJson;
            string familyName = generateWindow.FamilyName;
            SaveJson(familyJson, "api-response", familyName);
            string sourceImagePath = SaveSourceImages(generateWindow.SourceImages, familyName);

            // Save to history immediately after generation (before preview), so user can reopen even if they cancel preview
            SaveToHistory(familyName, sourceImagePath, generateWindow.TripoSRMeshPath,
                generateWindow.TripoSRTexturePath, familyJson, generateWindow.IsTripoSRMode,
                new GenerationResult { Success = true });

            var previewWindow = new PreviewWindow(familyJson, settingsManager.GetProvider(), apiKey, settingsManager.GetModelName(),
                generateWindow.SourceImages, sourceImagePath, familyName,
                generateWindow.TripoSRMeshPath, generateWindow.TripoSRTexturePath, generateWindow.IsTripoSRMode);
            new WindowInteropHelper(previewWindow).Owner = uiapp.MainWindowHandle;

            if (previewWindow.ShowDialog() == true && previewWindow.GenerateRequested)
            {
                familyName = previewWindow.FamilyName;
                string finalJson = InjectFamilyName(previewWindow.FinalFamilyJson, familyName);
                SaveJson(finalJson, "final", familyName);

                var generator = new FamilyGenerator();
                Autodesk.Revit.ApplicationServices.Application app = uiapp.Application;
                string texturePath = (settingsManager.GetApplyPhotoTexture()
                        && !string.IsNullOrEmpty(sourceImagePath) && File.Exists(sourceImagePath))
                    ? sourceImagePath : null;
                GenerationResult genResult = generator.Execute(doc, app, finalJson, texturePath);

                if (genResult.Success)
                {
                    // Update history with final JSON after successful Revit generation
                    SaveToHistory(familyName, sourceImagePath, generateWindow.TripoSRMeshPath,
                        generateWindow.TripoSRTexturePath, finalJson, generateWindow.IsTripoSRMode,
                        genResult);

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

        private static void SaveToHistory(string familyName, string sourceImagePath,
            string meshPath, string texturePath, string familyJson, bool isTripoSRMode,
            GenerationResult genResult)
        {
            try
            {
                var historyManager = new FamFabHistoryManager();
                string historyFolder = FamFabHistoryManager.GetHistoryFolderPath();
                Directory.CreateDirectory(historyFolder);

                string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                string slug = SanitizeFileName(familyName) ?? timestamp;

                // Copy source image to history
                string historyImagePath = null;
                if (!string.IsNullOrEmpty(sourceImagePath) && File.Exists(sourceImagePath))
                {
                    string ext = Path.GetExtension(sourceImagePath);
                    historyImagePath = Path.Combine(historyFolder, $"{slug}-source{ext}");
                    File.Copy(sourceImagePath, historyImagePath, true);
                }

                // Copy mesh to history
                string historyMeshPath = null;
                if (!string.IsNullOrEmpty(meshPath) && File.Exists(meshPath))
                {
                    historyMeshPath = Path.Combine(historyFolder, $"{slug}-mesh.obj");
                    File.Copy(meshPath, historyMeshPath, true);
                }

                // Copy texture to history
                string historyTexturePath = null;
                if (!string.IsNullOrEmpty(texturePath) && File.Exists(texturePath))
                {
                    historyTexturePath = Path.Combine(historyFolder, $"{slug}-texture.png");
                    File.Copy(texturePath, historyTexturePath, true);
                }

                // Save family JSON to history
                string historyJsonPath = Path.Combine(historyFolder, $"{slug}-family.json");
                File.WriteAllText(historyJsonPath, familyJson);

                historyManager.AddEntry(
                    familyName,
                    historyImagePath,
                    historyMeshPath,
                    historyTexturePath,
                    historyJsonPath,
                    isTripoSRMode ? "triposr" : "llm",
                    0, 0
                );
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to save history: {ex.Message}");
            }
        }

        private static string SaveSourceImages(IReadOnlyList<ImageInput> images, string familyName = null)
        {
            if (images == null || images.Count == 0) return null;
            string firstPath = null;
            for (int i = 0; i < images.Count; i++)
            {
                string path = SaveSourceImage(images[i].Bytes, images[i].MimeType, familyName, i, images.Count);
                if (i == 0) firstPath = path;
            }
            return firstPath;
        }

        private static string SaveSourceImage(byte[] imageBytes, string mimeType, string familyName, int index, int total)
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
                string suffix = total > 1 ? $"-source-{index + 1}" : "-source";
                string baseName = string.IsNullOrEmpty(slug)
                    ? $"{timestamp}{suffix}"
                    : $"{timestamp}-{slug}{suffix}";
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
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                using var ms = new System.IO.MemoryStream();
                using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
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
