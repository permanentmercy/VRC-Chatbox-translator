using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using VrcChatboxDemo.Services.Tts;

namespace VrcChatboxDemo.Services.Synthesis;

/// <summary>
/// IndexTTS 权重与音色文件的目录扫描。
///
/// 抽出来的原因：这些是「看文件系统」的纯查询，不参与服务端进程、HTTP 请求或声卡播放，
/// 却和它们挤在 1,800 行的 TtsService 里。现在它不持有任何状态，也不读单例配置：
/// 服务端脚本路径由调用方显式传入，日志通过回调注入，因此可以直接用临时目录测试。
/// </summary>
public static class TtsCatalog
{
    /// <summary>
    /// 定位 checkpoints 目录：与 tts_server.py 同级（IndexTTS 仓库的权重目录）。
    /// </summary>
    public static string? ResolveCheckpointsDir(string? serverScriptPath)
    {
        if (string.IsNullOrWhiteSpace(serverScriptPath)) return null;

        string? dir = Path.GetDirectoryName(serverScriptPath);
        if (string.IsNullOrEmpty(dir)) return null;

        return Path.Combine(dir, "checkpoints");
    }

    /// <summary>定位音色特征目录：与 checkpoints 同级的 prompts/。</summary>
    public static string? ResolvePromptsDir(string? serverScriptPath)
    {
        string? checkpoints = ResolveCheckpointsDir(serverScriptPath);
        if (checkpoints == null) return null;

        string? dir = Path.GetDirectoryName(checkpoints);
        if (string.IsNullOrEmpty(dir)) return null;

        return Path.Combine(dir, "prompts");
    }

    /// <summary>
    /// 扫描 prompts 目录里所有可用的音色特征（cond_mel）。
    /// 音色与权重解耦：权重决定咬字/韵律，音色决定声线，两者可自由组合。
    /// </summary>
    public static List<TtsPromptOption> DiscoverPrompts(string? serverScriptPath, Action<string>? log = null)
    {
        var list = new List<TtsPromptOption>();
        string? dir = ResolvePromptsDir(serverScriptPath);

        if (dir == null || !Directory.Exists(dir))
        {
            log?.Invoke($"未找到音色目录: {dir ?? "(路径无效)"}");
            return list;
        }

        try
        {
            foreach (var path in Directory.EnumerateFiles(dir, "*.pt"))
            {
                string name = Path.GetFileName(path);
                long size = 0;
                try { size = new FileInfo(path).Length; } catch { }

                string stem = Path.GetFileNameWithoutExtension(name);
                // X_prompt.pt 是「某个权重 X.pth 的配套音色」，去掉后缀就是它属于谁
                bool isPaired = stem.EndsWith("_prompt", StringComparison.OrdinalIgnoreCase);
                string owner = isPaired ? stem[..^"_prompt".Length] : "";

                string desc = isPaired
                    ? $"{size / 1024.0:F0} KB · 配套权重 {owner}.pth"
                    : $"{size / 1024.0:F0} KB · 独立音色";

                list.Add(new TtsPromptOption(
                    FileName: name,
                    DisplayName: $"{name}    ({desc})",
                    Description: desc,
                    SizeBytes: size));
            }

            // 配套音色在前（按权重名排），独立的排后面
            list.Sort((a, b) =>
            {
                bool pa = a.FileName.EndsWith("_prompt.pt", StringComparison.OrdinalIgnoreCase);
                bool pb = b.FileName.EndsWith("_prompt.pt", StringComparison.OrdinalIgnoreCase);
                if (pa != pb) return pa ? -1 : 1;
                return string.Compare(a.FileName, b.FileName, StringComparison.OrdinalIgnoreCase);
            });
        }
        catch (Exception ex)
        {
            log?.Invoke($"扫描音色目录异常: {ex.Message}");
        }

        return list;
    }

    /// <summary>判断某个音色文件是不是某个权重的配套音色（X.pth ↔ X_prompt.pt）。</summary>
    public static bool IsPromptPairedWith(string? promptFile, string? checkpointFile)
    {
        if (string.IsNullOrWhiteSpace(promptFile) || string.IsNullOrWhiteSpace(checkpointFile)) return false;
        string pStem = Path.GetFileNameWithoutExtension(promptFile);
        string cStem = Path.GetFileNameWithoutExtension(checkpointFile);
        return pStem.Equals(cStem + "_prompt", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 扫描 checkpoints 目录里所有可用的 GPT 主干权重。
    /// 排除 bigvgan_* / dvae*（声码器与 DVAE 权重）；是否 FP8 量化看同名 .json 边车。
    /// </summary>
    public static List<TtsModelOption> DiscoverModels(string? serverScriptPath, Action<string>? log = null)
    {
        var list = new List<TtsModelOption>();
        string? dir = ResolveCheckpointsDir(serverScriptPath);

        if (dir == null || !Directory.Exists(dir))
        {
            log?.Invoke($"未找到 checkpoints 目录: {dir ?? "(路径无效)"}");
            return list;
        }

        // 音色特征目录：服务端按 "prompts/<权重名>_prompt.pt" 的约定去找配套音色。
        // 没有配套文件的话，切换权重后声线不会变，这里要在 UI 上说清楚。
        string promptDir = Path.Combine(Path.GetDirectoryName(dir) ?? "", "prompts");

        try
        {
            foreach (var path in Directory.EnumerateFiles(dir, "*.pth"))
            {
                string name = Path.GetFileName(path);
                string lower = name.ToLowerInvariant();

                // 声码器 / DVAE 权重不是 GPT 推理权重，跳过
                if (lower.StartsWith("bigvgan") || lower.StartsWith("dvae")) continue;

                long size = 0;
                try { size = new FileInfo(path).Length; } catch { }

                bool quantized = IsQuantizedCheckpoint(path);

                string stem = Path.GetFileNameWithoutExtension(name);
                bool hasPrompt = !string.IsNullOrEmpty(stem) &&
                                 File.Exists(Path.Combine(promptDir, stem + "_prompt.pt"));

                // gpt.pth 之类没带 lora 后缀的，视为未微调的原始底模
                bool isBase = !lower.Contains("lora");

                string sizeText = size > 0 ? $"{size / 1024.0 / 1024 / 1024:F2} GB" : "大小未知";
                string tag = quantized
                    ? "FP8 量化 · 省显存"
                    : size >= 1_500_000_000L
                        ? "fp32 原始 · 加载后转 fp16"
                        : "fp16";

                string desc = $"{sizeText} · {tag}";
                if (isBase)
                {
                    desc += !hasPrompt
                        ? " · 原始底模，未学过任何特定音色"
                        : " · 原始底模";
                }
                else if (!hasPrompt)
                {
                    desc += " · ⚠️ 缺配套音色文件，切换后声线不变";
                }

                list.Add(new TtsModelOption(
                    FileName: name,
                    DisplayName: $"{name}    ({desc})",
                    Description: desc,
                    SizeBytes: size,
                    IsQuantized: quantized,
                    HasMatchingPrompt: hasPrompt,
                    IsBaseModel: isBase));
            }

            // 排序：有配套音色的微调权重优先 > 量化 > 名称；缺音色的和底模垫底
            list.Sort((a, b) =>
            {
                if (a.HasMatchingPrompt != b.HasMatchingPrompt) return a.HasMatchingPrompt ? -1 : 1;
                if (a.IsBaseModel != b.IsBaseModel) return a.IsBaseModel ? 1 : -1;
                if (a.IsQuantized != b.IsQuantized) return a.IsQuantized ? -1 : 1;
                return string.Compare(a.FileName, b.FileName, StringComparison.OrdinalIgnoreCase);
            });
        }
        catch (Exception ex)
        {
            log?.Invoke($"扫描权重目录异常: {ex.Message}");
        }

        return list;
    }

    /// <summary>判断某个 checkpoint 是否是 FP8 量化版（看同名 .json 边车里的 format 字段）。</summary>
    public static bool IsQuantizedCheckpoint(string checkpointPath)
    {
        try
        {
            string metaPath = Path.ChangeExtension(checkpointPath, ".json");
            if (!File.Exists(metaPath)) return false;

            using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
            return doc.RootElement.TryGetProperty("format", out var fmt) &&
                   (fmt.GetString() ?? "").Contains("fp8", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
