using System.Threading;
using System.Threading.Tasks;

namespace ArchSmarterFamFab.Data
{
    /// <summary>
    /// Locates the Python environment for TripoSR, checks that all required
    /// packages are importable, and installs them via uv or pip on demand.
    /// </summary>
    public static class TripoSRDependencyInstaller
    {
        /// <summary>
        /// Required packages that triposr_cli.py imports (or depends on at runtime).
        /// </summary>
        private static readonly string[] RequiredImports =
        {
            "torch",
            "numpy",
            "PIL",
            "xatlas",
            "rembg",
            "skimage",
            "trimesh",
            "einops",
            "huggingface_hub",
            "transformers",
            "safetensors",
            "omegaconf",
            "moderngl",
            "torchmcubes"
        };

        public class EnvironmentInfo
        {
            public string ProjectDir { get; set; }
            public string PythonExe { get; set; }
            public string UvExe { get; set; }
            public bool IsUvManaged => !string.IsNullOrEmpty(UvExe);
        }

        /// <summary>
        /// Finds the project directory and the best available Python/uv executables.
        /// </summary>
        public static EnvironmentInfo FindEnvironment()
        {
            string projectDir = FindProjectDir();
            string pythonExe = FindPythonExecutable(projectDir);
            string uvExe = FindUvExecutable();

            return new EnvironmentInfo
            {
                ProjectDir = projectDir,
                PythonExe = pythonExe,
                UvExe = uvExe
            };
        }

        /// <summary>
        /// Returns true if the given Python can import every required package.
        /// </summary>
        public static bool IsInstalled(string pythonExe)
        {
            if (string.IsNullOrEmpty(pythonExe) || !File.Exists(pythonExe))
                return false;

            string importStatement = string.Join(";", RequiredImports.Select(m => $"import {m}"));
            var psi = new ProcessStartInfo
            {
                FileName = pythonExe,
                Arguments = $"-c \"{importStatement}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            try
            {
                using var proc = Process.Start(psi);
                string stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit(60000);
                return proc.ExitCode == 0 && string.IsNullOrWhiteSpace(stderr);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Installs dependencies into the project directory, streaming output through
        /// <paramref name="progress"/>. Returns true on success.
        /// </summary>
        public static async Task<bool> InstallAsync(EnvironmentInfo env, IProgress<string> progress, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(env.ProjectDir) || !Directory.Exists(env.ProjectDir))
            {
                progress?.Report("Error: Project directory not found.");
                return false;
            }

            Directory.CreateDirectory(env.ProjectDir);

            if (!string.IsNullOrEmpty(env.UvExe))
            {
                progress?.Report($"Using uv: {env.UvExe}");
                return await RunUvSyncAsync(env, progress, ct);
            }

            if (!string.IsNullOrEmpty(env.PythonExe))
            {
                progress?.Report($"Using Python: {env.PythonExe}");
                return await RunPipInstallAsync(env, progress, ct);
            }

            progress?.Report("Error: Neither uv nor a usable Python interpreter was found.");
            return false;
        }

        private static async Task<bool> RunUvSyncAsync(EnvironmentInfo env, IProgress<string> progress, CancellationToken ct)
        {
            var psi = new ProcessStartInfo
            {
                FileName = env.UvExe,
                Arguments = "sync",
                WorkingDirectory = env.ProjectDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            return await RunProcessAsync(psi, progress, ct) == 0;
        }

        private static async Task<bool> RunPipInstallAsync(EnvironmentInfo env, IProgress<string> progress, CancellationToken ct)
        {
            string venvDir = Path.Combine(env.ProjectDir, ".venv");
            string venvPython = Path.Combine(venvDir, "Scripts", "python.exe");

            if (!File.Exists(venvPython))
            {
                progress?.Report("Creating virtual environment...");
                var venvPsi = new ProcessStartInfo
                {
                    FileName = env.PythonExe,
                    Arguments = $"-m venv \"{venvDir}\"",
                    WorkingDirectory = env.ProjectDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                if (await RunProcessAsync(venvPsi, progress, ct) != 0)
                {
                    progress?.Report("Error: Failed to create virtual environment.");
                    return false;
                }

                if (!File.Exists(venvPython))
                {
                    progress?.Report("Error: Virtual environment was not created.");
                    return false;
                }
            }

            progress?.Report("Upgrading pip...");
            var upgradePsi = new ProcessStartInfo
            {
                FileName = venvPython,
                Arguments = "-m pip install --upgrade pip",
                WorkingDirectory = env.ProjectDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            await RunProcessAsync(upgradePsi, progress, ct);

            progress?.Report("Installing packages from pyproject.toml...");
            var installPsi = new ProcessStartInfo
            {
                FileName = venvPython,
                Arguments = "-m pip install -e .",
                WorkingDirectory = env.ProjectDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            int exitCode = await RunProcessAsync(installPsi, progress, ct);
            if (exitCode != 0)
            {
                progress?.Report("pyproject.toml install failed; trying triposr/requirements.txt...");
                string reqPath = Path.Combine(env.ProjectDir, "triposr", "requirements.txt");
                if (File.Exists(reqPath))
                {
                    installPsi.Arguments = $"-m pip install -r \"{reqPath}\"";
                    exitCode = await RunProcessAsync(installPsi, progress, ct);
                }
            }

            return exitCode == 0;
        }

        private static async Task<int> RunProcessAsync(ProcessStartInfo psi, IProgress<string> progress, CancellationToken ct)
        {
            using var proc = new Process();
            proc.StartInfo = psi;

            var tcs = new TaskCompletionSource<int>();
            proc.EnableRaisingEvents = true;
            proc.Exited += (sender, args) => tcs.TrySetResult(proc.ExitCode);

            proc.OutputDataReceived += (sender, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                    progress?.Report(args.Data);
            };
            proc.ErrorDataReceived += (sender, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                    progress?.Report($"[stderr] {args.Data}");
            };

            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            using (ct.Register(() =>
            {
                try { proc.Kill(); } catch { /* ignored */ }
                tcs.TrySetCanceled();
            }))
            {
                try
                {
                    return await tcs.Task;
                }
                catch (OperationCanceledException)
                {
                    progress?.Report("Installation was cancelled.");
                    throw;
                }
            }
        }

        private static string FindProjectDir()
        {
            // Walk up from the assembly location looking for pyproject.toml.
            // This handles both the deployed add-in layout and the source repo layout.
            string current = GetAssemblyDir();
            for (int i = 0; i < 5 && !string.IsNullOrEmpty(current); i++)
            {
                if (File.Exists(Path.Combine(current, "pyproject.toml")))
                    return current;

                string parent = Path.GetDirectoryName(current);
                if (parent == current)
                    break;
                current = parent;
            }

            // Common manual deploy / development locations.
            string[] extraCandidates =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ArchSmarterFamFab"),
                Directory.GetCurrentDirectory()
            };

            foreach (var candidate in extraCandidates)
            {
                if (!string.IsNullOrEmpty(candidate) && File.Exists(Path.Combine(candidate, "pyproject.toml")))
                    return candidate;
            }

            // Fallback: return assembly dir so the user can at least see a meaningful error.
            return GetAssemblyDir();
        }

        private static string FindPythonExecutable(string projectDir)
        {
            // 1. Virtual environment in the project directory.
            string venvPython = Path.Combine(projectDir, ".venv", "Scripts", "python.exe");
            if (File.Exists(venvPython))
                return venvPython;

            // 2. Virtual environment in Documents/ArchSmarterFamFab.
            string docsVenv = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "ArchSmarterFamFab", ".venv", "Scripts", "python.exe");
            if (File.Exists(docsVenv))
                return docsVenv;

            // 3. uv-managed python in the project directory.
            if (!string.IsNullOrEmpty(projectDir))
            {
                string uvPython = FindUvPythonInFolder(projectDir);
                if (!string.IsNullOrEmpty(uvPython))
                    return uvPython;
            }

            // 4. uv-managed python in deployed/source directory.
            string uvPythonInAssemblyDir = FindUvPythonInFolder(GetAssemblyDir());
            if (!string.IsNullOrEmpty(uvPythonInAssemblyDir))
                return uvPythonInAssemblyDir;

            // 5. PATH lookup.
            foreach (var candidate in new[] { "python.exe", "python3.exe", "py.exe" })
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

        private static string FindUvExecutable()
        {
            // 1. PATH lookup.
            foreach (var candidate in new[] { "uv.exe", "uv" })
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

            // 2. Common uv install locations.
            string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string[] commonPaths =
            {
                Path.Combine(localData, "uv", "bin", "uv.exe"),
                Path.Combine(localData, "Programs", "uv", "uv.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cargo", "bin", "uv.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "uv.exe")
            };

            foreach (var path in commonPaths)
            {
                if (File.Exists(path))
                    return path;
            }

            return null;
        }

        private static string FindUvPythonInFolder(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
                return null;

            // uv run creates/uses a .venv by default.
            string uvVenvPython = Path.Combine(folderPath, ".venv", "Scripts", "python.exe");
            if (File.Exists(uvVenvPython))
                return uvVenvPython;

            // Ask uv where the managed interpreter is.
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "uv",
                    Arguments = "python find",
                    WorkingDirectory = folderPath,
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

        private static string GetAssemblyDir()
        {
            string location = Assembly.GetExecutingAssembly().Location;
            return Path.GetDirectoryName(location) ?? Directory.GetCurrentDirectory();
        }
    }
}
