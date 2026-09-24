using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Whisper.net.LibraryLoader;

namespace VrcChatboxDemo.Services;

public partial class SpeechService
{
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool SetDllDirectory(string lpPathName);

    private static bool _cudaEnvironmentConfigured = false;

    /// <summary>
    /// 检测系统是否存在可用的 CUDA 运行时环境 (包含官方 CUDA Toolkit 与 Ollama 内置打包的 cuda_v12/v11 驱动库)
    /// </summary>
    public static bool IsCudaRuntimeAvailable(out string? foundPath)
    {
        foundPath = null;
        try
        {
            // 1. 检查是否存在 NVIDIA 显卡核心驱动 (System32\nvcuda.dll)
            string system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string nvcudaDll = Path.Combine(system32, "nvcuda.dll");
            if (!File.Exists(nvcudaDll))
            {
                return false;
            }

            // 2. 检查环境变量 CUDA_PATH 及相关变体
            var envVars = Environment.GetEnvironmentVariables();
            foreach (string key in envVars.Keys)
            {
                if (key.StartsWith("CUDA_PATH", StringComparison.OrdinalIgnoreCase))
                {
                    string? val = envVars[key]?.ToString();
                    if (!string.IsNullOrEmpty(val))
                    {
                        string binDir = Path.Combine(val, "bin");
                        if (Directory.Exists(binDir) &&
                            (File.Exists(Path.Combine(binDir, "cublas64_12.dll")) ||
                             File.Exists(Path.Combine(binDir, "cudart64_12.dll")) ||
                             File.Exists(Path.Combine(binDir, "cublas64_11.dll"))))
                        {
                            foundPath = binDir;
                            return true;
                        }
                    }
                }
            }

            // 3. 检查系统 PATH 环境变量中的目录
            string? pathVar = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathVar))
            {
                foreach (var part in pathVar.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    try
                    {
                        if (Directory.Exists(part) &&
                            (File.Exists(Path.Combine(part, "cublas64_12.dll")) ||
                             File.Exists(Path.Combine(part, "cudart64_12.dll"))))
                        {
                            foundPath = part;
                            return true;
                        }
                    }
                    catch { }
                }
            }

            // 4. 检查 Ollama 内置打包的 CUDA 驱动/运行时目录
            var ollamaCandidates = new List<string>
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama", "lib", "ollama", "cuda_v12"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Ollama", "lib", "ollama", "cuda_v12"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama", "lib", "ollama", "cuda_v11"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Ollama", "lib", "ollama", "cuda_v11")
            };

            foreach (var dir in ollamaCandidates)
            {
                if (Directory.Exists(dir) &&
                    (File.Exists(Path.Combine(dir, "cublas64_12.dll")) ||
                     File.Exists(Path.Combine(dir, "cudart64_12.dll")) ||
                     File.Exists(Path.Combine(dir, "ggml-cuda.dll"))))
                {
                    foundPath = dir;
                    return true;
                }
            }
        }
        catch { }

        return false;
    }

    private static void EnsureCudaEnvironment()
    {
        if (_cudaEnvironmentConfigured) return;
        _cudaEnvironmentConfigured = true;

        try
        {
            if (IsCudaRuntimeAvailable(out string? cudaDir) && !string.IsNullOrEmpty(cudaDir))
            {
                SetDllDirectory(cudaDir);
                var curPath = Environment.GetEnvironmentVariable("PATH") ?? "";
                if (!curPath.Contains(cudaDir, StringComparison.OrdinalIgnoreCase))
                {
                    Environment.SetEnvironmentVariable("PATH", cudaDir + ";" + curPath);
                }
            }

            RuntimeOptions.RuntimeLibraryOrder = new List<RuntimeLibrary>
            {
                RuntimeLibrary.Cuda12,
                RuntimeLibrary.Vulkan,
                RuntimeLibrary.Cpu
            };
        }
        catch { }
    }
}
