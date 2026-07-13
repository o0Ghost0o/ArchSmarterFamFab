namespace ArchSmarterFamFab.Data
{
    public class TripoSRResult
    {
        public string MeshPath { get; set; }
        public string TexturePath { get; set; }
        public string ResultJsonPath { get; set; }
        public int VertexCount { get; set; }
        public int FaceCount { get; set; }
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
    }

    public class TripoSRClient
    {
        public static TripoSRResult Run(string imagePath, string outputDir, string familyName = null)
        {
            var env = TripoSRDependencyInstaller.FindEnvironment();

            string pythonExe = env.PythonExe;
            if (string.IsNullOrEmpty(pythonExe))
            {
                return new TripoSRResult
                {
                    Success = false,
                    ErrorMessage = "Python not found. Please install Python 3.11+ and restart Revit."
                };
            }

            string scriptPath = FindTriposrCliPath();
            if (string.IsNullOrEmpty(scriptPath))
            {
                return new TripoSRResult
                {
                    Success = false,
                    ErrorMessage = "TripoSR CLI script not found: triposr_cli.py"
                };
            }

            Directory.CreateDirectory(outputDir);

            var psi = new ProcessStartInfo
            {
                FileName = pythonExe,
                Arguments = $"\"{scriptPath}\" \"{imagePath}\" --output-dir \"{outputDir}\" --device auto --bake-texture --model-save-format obj",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? Directory.GetCurrentDirectory()
            };

            // If we are not using a venv python, try to use uv run so the project dependencies are available.
            if (!pythonExe.Contains(".venv") && !string.IsNullOrEmpty(env.UvExe))
            {
                string scriptDir = Path.GetDirectoryName(scriptPath);
                if (!string.IsNullOrEmpty(scriptDir) && File.Exists(Path.Combine(scriptDir, "pyproject.toml")))
                {
                    psi.FileName = env.UvExe;
                    psi.Arguments = $"run python \"{scriptPath}\" \"{imagePath}\" --output-dir \"{outputDir}\" --device auto --bake-texture --model-save-format obj";
                    psi.WorkingDirectory = scriptDir;
                }
            }

            try
            {
                using var process = Process.Start(psi);
                string stdout = process.StandardOutput.ReadToEnd();
                string stderr = process.StandardError.ReadToEnd();
                process.WaitForExit();

                if (process.ExitCode != 0)
                {
                    string errorMsg = stderr;
                    if (stderr.Contains("ModuleNotFoundError") || stderr.Contains("No module named"))
                    {
                        errorMsg = $"TripoSR dependencies are missing. Please restart the FamFab command to install them.\n\nDetails: {stderr}";
                    }
                    return new TripoSRResult
                    {
                        Success = false,
                        ErrorMessage = $"TripoSR process exited with code {process.ExitCode}. Error: {errorMsg}"
                    };
                }

                // Parse result JSON path from stdout (last line)
                string resultJsonPath = stdout.Trim().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
                if (string.IsNullOrEmpty(resultJsonPath) || !File.Exists(resultJsonPath))
                {
                    resultJsonPath = Path.Combine(outputDir, "result.json");
                }

                string meshPath = Path.Combine(outputDir, "mesh.obj");
                string texturePath = Path.Combine(outputDir, "texture.png");

                int vertices = 0, faces = 0;
                if (File.Exists(resultJsonPath))
                {
                    try
                    {
                        string json = File.ReadAllText(resultJsonPath);
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("vertices", out var vEl))
                            vertices = vEl.GetInt32();
                        if (doc.RootElement.TryGetProperty("faces", out var fEl))
                            faces = fEl.GetInt32();
                    }
                    catch { }
                }

                return new TripoSRResult
                {
                    Success = File.Exists(meshPath),
                    MeshPath = meshPath,
                    TexturePath = File.Exists(texturePath) ? texturePath : null,
                    ResultJsonPath = resultJsonPath,
                    VertexCount = vertices,
                    FaceCount = faces,
                    ErrorMessage = File.Exists(meshPath) ? null : "Mesh file was not generated."
                };
            }
            catch (Exception ex)
            {
                return new TripoSRResult
                {
                    Success = false,
                    ErrorMessage = $"TripoSR execution failed: {ex.Message}"
                };
            }
        }

        private static string FindTriposrCliPath()
        {
            string assemblyDir = Path.GetDirectoryName(typeof(TripoSRClient).Assembly.Location);

            // Walk up from the assembly location looking for triposr_cli.py.
            string current = assemblyDir;
            for (int i = 0; i < 5 && !string.IsNullOrEmpty(current); i++)
            {
                string path = Path.Combine(current, "triposr_cli.py");
                if (File.Exists(path))
                    return path;

                string parent = Path.GetDirectoryName(current);
                if (parent == current)
                    break;
                current = parent;
            }

            string docsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "ArchSmarterFamFab", "triposr_cli.py");
            if (File.Exists(docsPath))
                return docsPath;

            return null;
        }
    }
}
