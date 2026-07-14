using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ArchSmarterFamFab.Data
{
    /// <summary>
    /// Locates the Python environment for TripoSR and installs it on demand.
    /// The installer is uv-centric: it will download the uv package manager if it is
    /// not present, use uv to install Python 3.13 if necessary, and then install the
    /// project dependencies from pyproject.toml.
    /// </summary>
    public static class TripoSRDependencyInstaller
    {
        private const string TargetPythonVersion = "3.13";

        // uv release asset for 64-bit Windows. The /latest/download redirect follows
        // GitHub's latest release, so we always bootstrap a current uv build.
        private const string UvDownloadUrl = "https://github.com/astral-sh/uv/releases/latest/download/uv-x86_64-pc-windows-msvc.zip";

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
        /// Missing uv or Python will be installed on demand by <see cref="InstallAsync"/>.
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
            if (!CanRunPython(pythonExe))
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
        /// If uv is not present it is downloaded automatically. If Python 3.13 is not
        /// present it is installed automatically via uv.
        /// </summary>
        /// <param name="promptForCudaInstall">
        /// Optional callback invoked when CUDA build tools are missing. The argument is the
        /// prompt text; return true to open the CUDA Toolkit download page and retry.
        /// </param>
        public static async Task<bool> InstallAsync(EnvironmentInfo env, IProgress<string> progress, CancellationToken ct,
            Func<string, bool> promptForCudaInstall = null)
        {
            if (string.IsNullOrEmpty(env.ProjectDir) || !Directory.Exists(env.ProjectDir))
            {
                progress?.Report("Error: Project directory not found.");
                return false;
            }

            Directory.CreateDirectory(env.ProjectDir);

            string uvExe = env.UvExe;
            if (string.IsNullOrEmpty(uvExe))
            {
                uvExe = await EnsureUvAsync(progress, ct);
                if (string.IsNullOrEmpty(uvExe))
                {
                    progress?.Report("Error: The uv package manager could not be downloaded." +
                        " Please check your internet connection and try again.");
                    return false;
                }
            }

            progress?.Report($"Using uv: {uvExe}");

            // uv sync will download Python 3.13 automatically if it is not installed.
            return await RunUvSyncAsync(uvExe, env.ProjectDir, progress, ct, promptForCudaInstall);
        }

        private static async Task<string> EnsureUvAsync(IProgress<string> progress, CancellationToken ct)
        {
            string uvDir = GetUvInstallDir();
            string uvExe = Path.Combine(uvDir, "uv.exe");

            if (File.Exists(uvExe) && CanRunUv(uvExe))
                return uvExe;

            Directory.CreateDirectory(uvDir);
            string zipPath = Path.Combine(uvDir, "uv.zip");

            progress?.Report("Downloading uv package manager...");

            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(180) };
                var response = await client.GetAsync(UvDownloadUrl, ct);
                response.EnsureSuccessStatusCode();
                await File.WriteAllBytesAsync(zipPath, await response.Content.ReadAsByteArrayAsync(ct), ct);

                progress?.Report("Extracting uv...");
                ExtractZip(zipPath, uvDir);
            }
            catch (Exception ex)
            {
                progress?.Report($"Error downloading uv: {ex.Message}");
                return null;
            }
            finally
            {
                try { File.Delete(zipPath); } catch { }
            }

            if (File.Exists(uvExe) && CanRunUv(uvExe))
                return uvExe;

            progress?.Report("Error: uv was downloaded but does not run.");
            return null;
        }

        private static bool CanRunUv(string uvExe)
        {
            if (string.IsNullOrEmpty(uvExe) || !File.Exists(uvExe))
                return false;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = uvExe,
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit(10000);
                return proc?.ExitCode == 0;
            }
            catch { }

            return false;
        }

        private static async Task<bool> RunUvSyncAsync(string uvExe, string projectDir, IProgress<string> progress, CancellationToken ct,
            Func<string, bool> promptForCudaInstall)
        {
            // Try the GPU path first.
            if (await TryCudaInstallAsync(uvExe, projectDir, progress, ct))
                return true;

            // GPU path failed. Offer to install the CUDA Toolkit before falling back to CPU.
            string cudaPath = FindCudaPath();
            bool cudaInstalled = !string.IsNullOrEmpty(cudaPath);

            if (promptForCudaInstall != null)
            {
                string promptText = cudaInstalled
                    ? $"GPU build failed. CUDA Toolkit 12.6 was found at:\n{cudaPath}\n\nIf the build still fails after installing CUDA, restart your computer so the CUDA environment is fully registered, then run the installer again.\n\nWould you like to open the CUDA Toolkit download page anyway? If you choose No, FamFab will install CPU-only torch instead."
                    : "GPU build failed. CUDA Toolkit 12.6 is required for GPU acceleration.\n\nWould you like to open the CUDA Toolkit download page and install it now?\n\nAfter installing, restart your computer before running the installer again. If you choose No, FamFab will install CPU-only torch instead.";

                if (promptForCudaInstall(promptText))
                {
                    OpenCudaDownloadPage();

                    if (promptForCudaInstall("After installing CUDA Toolkit 12.6, restart your computer, then click OK to retry the GPU installation.\n\nIf you choose Cancel, FamFab will install CPU-only torch instead."))
                    {
                        if (await TryCudaInstallAsync(uvExe, projectDir, progress, ct))
                            return true;
                    }
                }
            }
            else if (cudaInstalled)
            {
                progress?.Report($"CUDA Toolkit 12.6 was found at {cudaPath}, but the GPU build still failed. Restart your computer and run the installer again.");
            }

            // Fall back to CPU-only torch.
            progress?.Report("Falling back to CPU-only torch...");
            return await RunCpuFallbackAsync(uvExe, projectDir, progress, ct);
        }

        private static void OpenCudaDownloadPage()
        {
            const string cudaDownloadUrl = "https://developer.nvidia.com/cuda-12-6-0-download-archive?target_os=Windows&target_arch=x86_64&target_version=11&target_type=exe_local";
            try
            {
                Process.Start(new ProcessStartInfo(cudaDownloadUrl) { UseShellExecute = true });
            }
            catch { }
        }

        private static async Task<bool> TryCudaInstallAsync(string uvExe, string projectDir, IProgress<string> progress, CancellationToken ct)
        {
            // Prefer CUDA 12.6 even if another version (e.g. 11.6) is earlier on PATH.
            string cudaPath = FindCudaPath();
            if (!string.IsNullOrEmpty(cudaPath))
                progress?.Report($"Using CUDA toolkit: {cudaPath}");

            // Always start from a clean venv so a broken previous attempt cannot interfere.
            progress?.Report("Creating virtual environment...");
            var venvPsi = new ProcessStartInfo
            {
                FileName = uvExe,
                Arguments = $"venv --clear --python {TargetPythonVersion}",
                WorkingDirectory = projectDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            PrepareCudaEnvironment(venvPsi, cudaPath);
            if (await RunProcessAsync(venvPsi, progress, ct) != 0)
            {
                progress?.Report("Error: Failed to create virtual environment.");
                return false;
            }

            // GPU path: install CUDA torch first so torchmcubes can build GPU extensions.
            // This requires the NVIDIA CUDA toolkit with Visual Studio integration.
            progress?.Report("Installing torch (CUDA 12.6) so torchmcubes can build...");
            var torchPsi = new ProcessStartInfo
            {
                FileName = uvExe,
                Arguments = $"pip install torch --python {TargetPythonVersion} --index-url https://download.pytorch.org/whl/cu126",
                WorkingDirectory = projectDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            PrepareCudaEnvironment(torchPsi, cudaPath);
            int torchExit = await RunProcessAsync(torchPsi, progress, ct);
            if (torchExit != 0)
            {
                progress?.Report("CUDA torch install failed.");
                return false;
            }

            // --no-install-project skips the (empty) famfab-triposr package itself.
            // torchmcubes is installed separately after this sync so we can patch it.
            // --upgrade re-resolves against pyproject.toml and ignores stale locks.
            progress?.Report("Installing remaining dependencies (this may take several minutes)...");
            var syncPsi = new ProcessStartInfo
            {
                FileName = uvExe,
                Arguments = $"sync --no-install-project --no-build-isolation --upgrade --python {TargetPythonVersion}",
                WorkingDirectory = projectDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            PrepareCudaEnvironment(syncPsi, cudaPath);

            var (exitCode, output) = await RunProcessAndCaptureAsync(syncPsi, progress, ct);
            if (exitCode == 0)
            {
                // uv sync may have removed build tools because they are not in pyproject.toml.
                // Re-install them now, after sync, so torchmcubes can build.
                if (!await InstallBuildToolsAsync(uvExe, projectDir, cudaPath, progress, ct))
                    return false;

                // Dependencies installed; now build the patched torchmcubes extension.
                var (mcubesOk, mcubesOutput) = await InstallPatchedTorchmcubesAsync(uvExe, projectDir, TargetPythonVersion, cudaPath, useNinja: true, progress, ct);
                if (mcubesOk)
                    return true;

                // torchmcubes build failed. If it looks CUDA-related, fall back to CPU.
                if (LooksLikeCudaBuildFailure(mcubesOutput))
                {
                    progress?.Report("CUDA build failed. If CUDA Toolkit 12.6 is already installed, try restarting your computer so the CUDA environment is fully registered, then retry.");
                    return false;
                }

                return false;
            }

            // If the failure looks CUDA-related, report it clearly and give up on the GPU path.
            if (LooksLikeCudaBuildFailure(output))
            {
                progress?.Report("CUDA build failed. If CUDA Toolkit 12.6 is already installed, try restarting your computer so the CUDA environment is fully registered, then retry.");
                return false;
            }

            // Final fallback within the GPU path: use uv pip install -e . with build isolation disabled.
            progress?.Report("uv sync failed; trying uv pip install...");
            var pipPsi = new ProcessStartInfo
            {
                FileName = uvExe,
                Arguments = $"pip install -e . --no-build-isolation --python {TargetPythonVersion}",
                WorkingDirectory = projectDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            PrepareCudaEnvironment(pipPsi, cudaPath);
            if (await RunProcessAsync(pipPsi, progress, ct) != 0)
                return false;

            // After the fallback dependency install, build torchmcubes.
            if (!await InstallBuildToolsAsync(uvExe, projectDir, cudaPath, progress, ct))
                return false;

            var (fallbackMcubesOk, fallbackMcubesOutput) = await InstallPatchedTorchmcubesAsync(uvExe, projectDir, TargetPythonVersion, cudaPath, useNinja: true, progress, ct);
            if (fallbackMcubesOk)
                return true;

            if (LooksLikeCudaBuildFailure(fallbackMcubesOutput))
            {
                progress?.Report("CUDA build failed. If CUDA Toolkit 12.6 is already installed, try restarting your computer so the CUDA environment is fully registered, then retry.");
            }

            return false;
        }

        /// <summary>
        /// Installs the build tools required to compile torchmcubes. Kept separate from
        /// the main dependency sync because uv sync removes packages not listed in
        /// pyproject.toml.
        /// </summary>
        private static async Task<bool> InstallBuildToolsAsync(string uvExe, string projectDir, string cudaPath, IProgress<string> progress, CancellationToken ct)
        {
            progress?.Report("Installing build tools...");
            var buildToolsPsi = new ProcessStartInfo
            {
                FileName = uvExe,
                Arguments = $"pip install setuptools wheel cmake ninja scikit-build-core \"pybind11[global]\" --python {TargetPythonVersion}",
                WorkingDirectory = projectDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            PrepareCudaEnvironment(buildToolsPsi, cudaPath);
            return await RunProcessAsync(buildToolsPsi, progress, ct) == 0;
        }

        private static bool LooksLikeCudaBuildFailure(string output)
        {
            if (string.IsNullOrWhiteSpace(output))
                return false;

            string[] markers =
            {
                "No CUDA toolset found",
                "No CUDA compiler found",
                "CUDA_TOOLKIT_ROOT_DIR not found",
                "Could not find a package configuration file provided by \"CUDAToolkit\"",
                "No CUDA installation found",
                "nvcc fatal",
                "Cannot find CUDA",
            };

            string lowered = output.ToLowerInvariant();
            foreach (string marker in markers)
            {
                if (lowered.Contains(marker.ToLowerInvariant()))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Finds a CUDA 12.6 toolkit installation. Returns null if none is found.
        /// </summary>
        private static string FindCudaPath()
        {
            // If CUDA_PATH already points to 12.6, use it.
            string cudaPath = Environment.GetEnvironmentVariable("CUDA_PATH");
            if (!string.IsNullOrEmpty(cudaPath) && cudaPath.Contains("v12.6"))
                return cudaPath;

            // Default install location.
            string defaultPath = @"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.6";
            if (Directory.Exists(defaultPath) && File.Exists(Path.Combine(defaultPath, "bin", "nvcc.exe")))
                return defaultPath;

            // Search for any v12.6 install under the CUDA base directory.
            string baseDir = @"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA";
            if (Directory.Exists(baseDir))
            {
                foreach (string dir in Directory.GetDirectories(baseDir, "v12.6*"))
                {
                    if (File.Exists(Path.Combine(dir, "bin", "nvcc.exe")))
                        return dir;
                }
            }

            return null;
        }

        /// <summary>
        /// Sets CUDA_PATH and prepends the CUDA bin directory to PATH on the process
        /// start info so CMake finds the requested toolkit even if another CUDA version
        /// is earlier on the user's PATH.
        /// </summary>
        private static void PrepareCudaEnvironment(ProcessStartInfo psi, string cudaPath)
        {
            if (string.IsNullOrEmpty(cudaPath))
                return;

            var env = psi.EnvironmentVariables;
            env["CUDA_PATH"] = cudaPath;
            env["CUDA_PATH_V12_6"] = cudaPath;

            string binPath = Path.Combine(cudaPath, "bin");
            string existingPath = env["PATH"] ?? string.Empty;
            string[] pathEntries = existingPath.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            bool alreadyPresent = false;
            foreach (string entry in pathEntries)
            {
                if (entry.TrimEnd('\\').Equals(binPath, StringComparison.OrdinalIgnoreCase))
                {
                    alreadyPresent = true;
                    break;
                }
            }

            if (!alreadyPresent)
                env["PATH"] = binPath + ";" + existingPath;
        }

        private static async Task<bool> RunCpuFallbackAsync(string uvExe, string projectDir, IProgress<string> progress, CancellationToken ct)
        {
            progress?.Report("Switching to CPU-only torch. TripoSR will run on CPU.");

            // Clear venv so CUDA torch and any partial installs are gone.
            progress?.Report("Recreating virtual environment...");
            var venvPsi = new ProcessStartInfo
            {
                FileName = uvExe,
                Arguments = $"venv --clear --python {TargetPythonVersion}",
                WorkingDirectory = projectDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            if (await RunProcessAsync(venvPsi, progress, ct) != 0)
            {
                progress?.Report("Error: Failed to recreate virtual environment.");
                return false;
            }

            // Install CPU torch. Use --no-config so pyproject.toml's CUDA source pin
            // for torch is ignored during the CPU fallback.
            progress?.Report("Installing torch (CPU)...");
            var torchPsi = new ProcessStartInfo
            {
                FileName = uvExe,
                Arguments = $"pip install torch --python {TargetPythonVersion} --python-version {TargetPythonVersion} --resolution highest --index-url https://download.pytorch.org/whl/cpu --no-config",
                WorkingDirectory = projectDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            if (await RunProcessAsync(torchPsi, progress, ct) != 0)
            {
                progress?.Report("Error: CPU torch could not be installed.");
                return false;
            }

            // Install build tools. --no-config and --python-version keep the resolution
            // consistent with the clean venv Python 3.13.
            progress?.Report("Installing build tools...");
            var buildToolsPsi = new ProcessStartInfo
            {
                FileName = uvExe,
                Arguments = $"pip install setuptools wheel cmake ninja scikit-build-core \"pybind11[global]\" --python {TargetPythonVersion} --python-version {TargetPythonVersion} --resolution highest --no-config",
                WorkingDirectory = projectDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            if (await RunProcessAsync(buildToolsPsi, progress, ct) != 0)
            {
                progress?.Report("Error: Failed to install build tools.");
                return false;
            }

            // Install the remaining dependencies from pyproject.toml, excluding torch
            // (already installed) and torchmcubes (built separately). --no-config keeps
            // pyproject.toml's CUDA torch source from interfering. Explicit numba/llvmlite
            // lower bounds ensure Python 3.13 compatible versions are selected.
            progress?.Report("Installing remaining dependencies...");
            string[] deps = GetPyprojectDependencies(projectDir);
            if (deps.Length > 0)
            {
                var depsPsi = new ProcessStartInfo
                {
                    FileName = uvExe,
                    Arguments = $"pip install \"numba>=0.60.0\" \"llvmlite>=0.43.0\" {string.Join(" ", deps)} --python {TargetPythonVersion} --python-version {TargetPythonVersion} --resolution highest --no-config",
                    WorkingDirectory = projectDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                if (await RunProcessAsync(depsPsi, progress, ct) != 0)
                {
                    progress?.Report("Error: Failed to install dependencies.");
                    return false;
                }
            }

            // Build patched torchmcubes against CPU torch.
            progress?.Report("Building torchmcubes (CPU)...");
            var (cpuMcubesOk, _) = await InstallPatchedTorchmcubesAsync(uvExe, projectDir, TargetPythonVersion, cudaPath: null, useNinja: false, progress, ct);
            return cpuMcubesOk;
        }

        /// <summary>
        /// Clones torchmcubes from GitHub, applies Windows build patches
        /// (C++20 for current torch headers; Ninja generator only for the CUDA path),
        /// then installs it into the active uv environment.
        /// Returns whether the build succeeded and the captured output for diagnostics.
        /// </summary>
        private static async Task<(bool Success, string Output)> InstallPatchedTorchmcubesAsync(string uvExe, string projectDir, string pythonVersion, string cudaPath, bool useNinja,
            IProgress<string> progress, CancellationToken ct)
        {
            string cloneDir = Path.Combine(Path.GetTempPath(), $"famfab-torchmcubes-{Guid.NewGuid():N}");
            Directory.CreateDirectory(cloneDir);
            try
            {
                progress?.Report("Cloning torchmcubes source...");
                var clonePsi = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = "clone --depth 1 https://github.com/tatsy/torchmcubes.git .",
                    WorkingDirectory = cloneDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                PrepareCudaEnvironment(clonePsi, cudaPath);
                if (await RunProcessAsync(clonePsi, progress, ct) != 0)
                {
                    progress?.Report("Error: Failed to clone torchmcubes. Make sure git is installed and available on PATH.");
                    return (false, string.Empty);
                }

                PatchTorchmcubesSource(cloneDir, useNinja);

                progress?.Report("Building torchmcubes...");
                string installArgs = string.IsNullOrEmpty(cudaPath)
                    ? $"pip install \"{cloneDir}\" --python {pythonVersion} --python-version {pythonVersion} --resolution highest --no-build-isolation --no-config"
                    : $"pip install \"{cloneDir}\" --python {pythonVersion} --no-build-isolation";

                // The CUDA path uses Ninja to avoid the Visual Studio CUDA toolset
                // requirement. Ninja needs the MSVC environment (INCLUDE/LIB/PATH) to
                // find cl.exe, so run the build through vcvars64.bat when available.
                if (!string.IsNullOrEmpty(cudaPath))
                {
                    string vcvarsPath = FindVcvarsPath();
                    if (string.IsNullOrEmpty(vcvarsPath))
                    {
                        progress?.Report("Warning: Could not find vcvars64.bat. The CUDA build may fail if cl.exe is not on PATH.");
                    }
                    else
                    {
                        string batchPath = Path.Combine(cloneDir, "build_mcubes.bat");
                        string batchContent = $@"@echo off{Environment.NewLine}call ""{vcvarsPath}""{Environment.NewLine}if errorlevel 1 exit /b 1{Environment.NewLine}""{uvExe}"" {installArgs}";
                        File.WriteAllText(batchPath, batchContent);

                        var batchPsi = new ProcessStartInfo
                        {
                            FileName = "cmd.exe",
                            Arguments = $"/C \"{batchPath}\"",
                            WorkingDirectory = projectDir,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        PrepareCudaEnvironment(batchPsi, cudaPath);
                        var (batchExit, batchOutput) = await RunProcessAndCaptureAsync(batchPsi, progress, ct);
                        return (batchExit == 0, batchOutput);
                    }
                }

                var installPsi = new ProcessStartInfo
                {
                    FileName = uvExe,
                    Arguments = installArgs,
                    WorkingDirectory = projectDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                PrepareCudaEnvironment(installPsi, cudaPath);
                var (exitCode, output) = await RunProcessAndCaptureAsync(installPsi, progress, ct);
                return (exitCode == 0, output);
            }
            finally
            {
                try { Directory.Delete(cloneDir, recursive: true); } catch { }
            }
        }

        /// <summary>
        /// Finds the Visual Studio x64 environment setup script (vcvars64.bat).
        /// Uses vswhere when available, otherwise checks common installation paths.
        /// </summary>
        private static string FindVcvarsPath()
        {
            string vswhere = @"C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe";
            if (File.Exists(vswhere))
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = vswhere,
                        Arguments = "-latest -products * -property installationPath",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var proc = Process.Start(psi);
                    string path = proc.StandardOutput.ReadToEnd().Trim();
                    proc.WaitForExit(10000);
                    if (proc.ExitCode == 0 && !string.IsNullOrEmpty(path))
                    {
                        string candidate = Path.Combine(path, "VC", "Auxiliary", "Build", "vcvars64.bat");
                        if (File.Exists(candidate))
                            return candidate;
                    }
                }
                catch { }
            }

            string[] commonPaths =
            {
                @"C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat",
                @"C:\Program Files\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat",
                @"C:\Program Files (x86)\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat",
                @"C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat",
                @"C:\Program Files (x86)\Microsoft Visual Studio\2022\Professional\VC\Auxiliary\Build\vcvars64.bat",
                @"C:\Program Files\Microsoft Visual Studio\2022\Professional\VC\Auxiliary\Build\vcvars64.bat",
                @"C:\Program Files (x86)\Microsoft Visual Studio\2022\Enterprise\VC\Auxiliary\Build\vcvars64.bat",
                @"C:\Program Files\Microsoft Visual Studio\2022\Enterprise\VC\Auxiliary\Build\vcvars64.bat",
            };

            foreach (string candidate in commonPaths)
            {
                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
        }

        /// <summary>
        /// Patches the cloned torchmcubes source so it builds on Windows with current
        /// PyTorch wheels: CMakeLists.txt is bumped to C++20, and pyproject.toml is
        /// updated to force the Ninja generator when requested.
        /// </summary>
        private static void PatchTorchmcubesSource(string cloneDir, bool useNinja)
        {
            string cmakePath = Path.Combine(cloneDir, "CMakeLists.txt");
            if (File.Exists(cmakePath))
            {
                string content = File.ReadAllText(cmakePath);
                content = content.Replace("set(CMAKE_CXX_STANDARD 17)", "set(CMAKE_CXX_STANDARD 20)");
                File.WriteAllText(cmakePath, content);
            }

            string pyprojectPath = Path.Combine(cloneDir, "pyproject.toml");
            if (File.Exists(pyprojectPath) && useNinja)
            {
                string content = File.ReadAllText(pyprojectPath);
                content = content.Replace("args = []", "args = [\"-G\", \"Ninja\"]");
                File.WriteAllText(pyprojectPath, content);
            }
        }

        private static string[] GetPyprojectDependencies(string projectDir)
        {
            string pyprojectPath = Path.Combine(projectDir, "pyproject.toml");
            if (!File.Exists(pyprojectPath))
                return Array.Empty<string>();

            string pythonExe = Path.Combine(projectDir, ".venv", "Scripts", "python.exe");
            if (!File.Exists(pythonExe))
                return Array.Empty<string>();

            // tomllib is part of the Python 3.13 stdlib.
            string escapedPath = pyprojectPath.Replace("\\", "\\\\");
            string script = $@"
import tomllib, sys
with open(r'{escapedPath}', 'rb') as f:
    deps = tomllib.load(f)['project']['dependencies']
for d in deps:
    if not d.startswith('torch') and not d.startswith('torchmcubes'):
        print(d)
";
            var psi = new ProcessStartInfo
            {
                FileName = pythonExe,
                Arguments = $"-c \"{script}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            try
            {
                using var proc = Process.Start(psi);
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(30000);
                if (proc.ExitCode != 0)
                    return Array.Empty<string>();

                return output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            }
            catch
            {
                return Array.Empty<string>();
            }
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

        private static async Task<(int ExitCode, string Output)> RunProcessAndCaptureAsync(ProcessStartInfo psi, IProgress<string> progress, CancellationToken ct)
        {
            var outputBuilder = new StringBuilder();
            using var proc = new Process();
            proc.StartInfo = psi;

            var tcs = new TaskCompletionSource<int>();
            proc.EnableRaisingEvents = true;
            proc.Exited += (sender, args) => tcs.TrySetResult(proc.ExitCode);

            proc.OutputDataReceived += (sender, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                {
                    progress?.Report(args.Data);
                    outputBuilder.AppendLine(args.Data);
                }
            };
            proc.ErrorDataReceived += (sender, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                {
                    progress?.Report($"[stderr] {args.Data}");
                    outputBuilder.AppendLine(args.Data);
                }
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
                    int exitCode = await tcs.Task;
                    return (exitCode, outputBuilder.ToString());
                }
                catch (OperationCanceledException)
                {
                    progress?.Report("Installation was cancelled.");
                    throw;
                }
            }
        }

        /// <summary>
        /// Returns true if the executable actually responds to "python --version".
        /// This catches broken uv trampolines whose real interpreter is missing.
        /// </summary>
        private static bool CanRunPython(string pythonExe)
        {
            if (string.IsNullOrEmpty(pythonExe) || !File.Exists(pythonExe))
                return false;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = pythonExe,
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit(10000);
                return proc?.ExitCode == 0;
            }
            catch { }

            return false;
        }

        private static string GetUvInstallDir()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "ArchSmarter", "FamFab", "uv");
        }

        private static void ExtractZip(string zipPath, string extractDir)
        {
            using var archive = ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries)
            {
                string destPath = Path.Combine(extractDir, entry.FullName);
                string destDir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(destDir))
                    Directory.CreateDirectory(destDir);

                if (string.IsNullOrEmpty(entry.Name))
                    continue; // directory entry

                entry.ExtractToFile(destPath, overwrite: true);
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
            if (CanRunPython(venvPython))
                return venvPython;

            // 2. Virtual environment in Documents/ArchSmarterFamFab.
            string docsVenv = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "ArchSmarterFamFab", ".venv", "Scripts", "python.exe");
            if (CanRunPython(docsVenv))
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
            // 1. Local copy managed by the add-in.
            string localUv = Path.Combine(GetUvInstallDir(), "uv.exe");
            if (File.Exists(localUv))
                return localUv;

            // 2. PATH lookup.
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

            // 3. Common uv install locations.
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
            if (CanRunPython(uvVenvPython))
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
                        if (CanRunPython(path))
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
