using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VrcChatboxDemo.Services;

public class TranslationResult
{
    public string Text { get; set; } = string.Empty;
    public long LatencyMs { get; set; }
}

public class OllamaService
{
    private readonly HttpClient _httpClient;

    public OllamaService()
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
    }

    public async Task<TranslationResult> TranslateAsync(string text, string targetLanguage, string model, string endpoint, string? promptTemplate = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return new TranslationResult();

        var sw = Stopwatch.StartNew();
        string cleanEndpoint = (endpoint ?? "http://127.0.0.1:11434").TrimEnd('/');
        string url = $"{cleanEndpoint}/api/generate";

        string prompt;
        if (!string.IsNullOrWhiteSpace(promptTemplate))
        {
            prompt = promptTemplate.Replace("{targetLanguage}", targetLanguage);
            if (prompt.Contains("{text}"))
            {
                prompt = prompt.Replace("{text}", text.Trim());
            }
            else
            {
                prompt = prompt.TrimEnd() + "\n\n" + text.Trim();
            }
        }
        else
        {
            prompt = $"Translate the following text into {targetLanguage}. Output ONLY the translated text without explanation, notes, or quotes:\n\n{text.Trim()}";
        }

        var payload = new
        {
            model = string.IsNullOrWhiteSpace(model) ? "RogerBen/HY-MT2-1.8B:latest" : model.Trim(),
            prompt = prompt,
            stream = false
        };

        string jsonPayload = JsonSerializer.Serialize(payload);
        using var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync(url, content, cancellationToken);
        response.EnsureSuccessStatusCode();

        string jsonResponse = await response.Content.ReadAsStringAsync(cancellationToken);
        sw.Stop();

        using var doc = JsonDocument.Parse(jsonResponse);
        if (doc.RootElement.TryGetProperty("response", out var respElement))
        {
            return new TranslationResult
            {
                Text = respElement.GetString()?.Trim() ?? string.Empty,
                LatencyMs = sw.ElapsedMilliseconds
            };
        }

        return new TranslationResult { LatencyMs = sw.ElapsedMilliseconds };
    }

    public async Task<List<string>> GetInstalledModelsAsync(string endpoint, CancellationToken cancellationToken = default)
    {
        var models = new List<string>();
        try
        {
            string cleanEndpoint = (endpoint ?? "http://127.0.0.1:11434").TrimEnd('/');
            string url = $"{cleanEndpoint}/api/tags";

            var response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode) return models;

            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("models", out var modelsArray) && modelsArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in modelsArray.EnumerateArray())
                {
                    if (item.TryGetProperty("name", out var nameProp))
                    {
                        string? name = nameProp.GetString();
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            models.Add(name);
                        }
                    }
                }
            }
        }
        catch
        {
            // Ignore if Ollama offline
        }

        return models;
    }

    /// <summary>
    /// 请求 Ollama 立即将指定模型从显存中卸载丢弃 (keep_alive: 0)
    /// </summary>
    public async Task<bool> UnloadModelAsync(string model, string endpoint, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(model)) return false;
        try
        {
            string cleanEndpoint = (endpoint ?? "http://127.0.0.1:11434").TrimEnd('/');
            string url = $"{cleanEndpoint}/api/generate";

            var payload = new
            {
                model = model.Trim(),
                keep_alive = 0
            };

            string jsonPayload = JsonSerializer.Serialize(payload);
            using var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            var response = await _httpClient.PostAsync(url, content, cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 查询 Ollama 当前正在驻留显存的所有模型，并逐一发送请求彻底释放显存
    /// </summary>
    public async Task<int> UnloadAllModelsAsync(string endpoint, CancellationToken cancellationToken = default)
    {
        int unloadedCount = 0;
        try
        {
            string cleanEndpoint = (endpoint ?? "http://127.0.0.1:11434").TrimEnd('/');
            string psUrl = $"{cleanEndpoint}/api/ps";

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            var response = await _httpClient.GetAsync(psUrl, cts.Token);
            if (!response.IsSuccessStatusCode) return 0;

            string json = await response.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("models", out var modelsArray) && modelsArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in modelsArray.EnumerateArray())
                {
                    if (item.TryGetProperty("name", out var nameProp))
                    {
                        string? name = nameProp.GetString();
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            bool success = await UnloadModelAsync(name, cleanEndpoint, cancellationToken);
                            if (success) unloadedCount++;
                        }
                    }
                }
            }
        }
        catch
        {
            // Ignore if Ollama offline
        }

        return unloadedCount;
    }
}

