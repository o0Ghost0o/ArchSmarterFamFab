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
            string pythonExe = FindPythonExecutable();
            if (string.IsNullOrEmpty(pythonExe))
            {
                return new TripoSRResult
                {
                    Success = false,
                    ErrorMessage = "Python not found. Please install Python 3.11+ and uv, then run 'uv sync' in the project folder."
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
                Arguments = $"\"{scriptPath}\" \"{imagePath}\" --output-dir \"{outputDir}\" --device cpu --bake-texture --model-save-format obj",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? Directory.GetCurrentDirectory()
            };

            try
            {
                using var process = Process.Start(psi);
                string stdout = process.StandardOutput.ReadToEnd();
                string stderr = process.StandardError.ReadToEnd();
                process.WaitForExit();

                if (process.ExitCode != 0)
                {
                    // Detect common dependency errors from stderr
                    string errorMsg = stderr;
                    if (stderr.Contains("ModuleNotFoundError") || stderr.Contains("No module named"))
                    {
                        errorMsg = $"TripoSR dependencies missing. Please run 'uv sync' in the project folder, or: pip install torch numpy pillow xatlas rembg scikit-image\n\nDetails: {stderr}";
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
                    // Fallback: look for result.json in output dir
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

        private static string FindPythonExecutable()
        {
            // 1. Check for .venv in project directory
            string projectVenv = Path.Combine(
                Path.GetDirectoryName(typeof(TripoSRClient).Assembly.Location) ?? Directory.GetCurrentDirectory(),
                ".venv", "Scripts", "python.exe");
            if (File.Exists(projectVenv))
                return projectVenv;

            // 2. Check for .venv in Documents/ArchSmarterFamFab
            string docsVenv = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "ArchSmarterFamFab", ".venv", "Scripts", "python.exe");
            if (File.Exists(docsVenv))
                return docsVenv;

            // 3. Check for uv-managed python
            string uvPython = FindUvPython();
            if (!string.IsNullOrEmpty(uvPython))
                return uvPython;

            // 4. Check PATH for python
            string[] candidates = { "python.exe", "python3.exe", "py.exe" };
            foreach (var candidate in candidates)
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = candidate,
                        Arguments = "--version",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var proc = Process.Start(psi);
                    proc?.WaitForExit(5000);
                    if (proc?.ExitCode == 0)
                        return candidate;
                }
                catch { }
            }

            return null;
        }

        private static string FindUvPython()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "uv",
                    Arguments = "python find",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    string output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(10000);
                    if (proc.ExitCode == 0)
                    {
                        string path = output.Trim();
                        if (File.Exists(path))
                            return path;
                    }
                }
            }
            catch { }
            return null;
        }

        private static string FindTriposrCliPath()
        {
            string assemblyDir = Path.GetDirectoryName(typeof(TripoSRClient).Assembly.Location);
            if (!string.IsNullOrEmpty(assemblyDir))
            {
                string path = Path.Combine(assemblyDir, "triposr_cli.py");
                if (File.Exists(path))
                    return path;
            }

            string docsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "ArchSmarterFamFab", "triposr_cli.py");
            if (File.Exists(docsPath))
                return docsPath;

            // Check if running from project source
            string sourcePath = Path.Combine(
                Path.GetDirectoryName(assemblyDir) ?? "",
                "triposr_cli.py");
            if (File.Exists(sourcePath))
                return sourcePath;

            return null;
        }
    }
}
