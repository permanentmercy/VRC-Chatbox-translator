using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VrcChatboxDemo.Services;

public partial class InboundService
{
    private void StartHttpServer(CancellationToken ct)
    {
        try
        {
            _httpListener = new HttpListener();
            _httpListener.Prefixes.Add($"http://127.0.0.1:{HttpPort}/");
            _httpListener.Prefixes.Add($"http://localhost:{HttpPort}/");
            _httpListener.Start();

            _ = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested && _httpListener != null && _httpListener.IsListening)
                {
                    try
                    {
                        var context = await _httpListener.GetContextAsync();
                        _ = HandleHttpRequestAsync(context);
                    }
                    catch (HttpListenerException) { break; }
                    catch (ObjectDisposedException) { break; }
                    catch { }
                }
            }, ct);
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"HTTP 外部接口启动异常 ({HttpPort}): {ex.Message}");
        }
    }

    private async Task HandleHttpRequestAsync(HttpListenerContext context)
    {
        try
        {
            var request = context.Request;
            var response = context.Response;

            // 跨域支持 (CORS)
            response.Headers.Add("Access-Control-Allow-Origin", "*");
            response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

            if (request.HttpMethod.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                response.StatusCode = 200;
                response.Close();
                return;
            }

            string rawPath = request.Url?.AbsolutePath?.ToLowerInvariant() ?? "/";

            // ----------------------------------------------------
            // 1. 变量 API 路由: /api/variables
            // ----------------------------------------------------
            if (rawPath.StartsWith("/api/variables"))
            {
                // GET /api/variables (查询所有变量)
                if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                    (rawPath == "/api/variables" || rawPath == "/api/variables/"))
                {
                    var vars = VariableService?.GetAllVariables() ?? new List<VariableItem>();
                    var resData = new { success = true, variables = vars };
                    var jsonBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(resData));
                    response.ContentType = "application/json; charset=utf-8";
                    response.StatusCode = 200;
                    await response.OutputStream.WriteAsync(jsonBytes);
                    return;
                }

                // GET /api/variables/set?name=xxx&value=yyy
                if (rawPath.StartsWith("/api/variables/set"))
                {
                    string name = request.QueryString["name"] ?? string.Empty;
                    string value = request.QueryString["value"] ?? request.QueryString["val"] ?? string.Empty;
                    string? disp = request.QueryString["display"] ?? request.QueryString["displayName"];
                    string? desc = request.QueryString["desc"] ?? request.QueryString["description"];

                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        var item = VariableService?.SetVariable(name, value, disp, desc);
                        StatusChanged?.Invoke($"外部变量更新: {{{name}}} = \"{value}\"");
                        var jsonBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { success = true, variable = item }));
                        response.ContentType = "application/json; charset=utf-8";
                        response.StatusCode = 200;
                        await response.OutputStream.WriteAsync(jsonBytes);
                    }
                    else
                    {
                        var jsonBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { success = false, error = "Missing 'name' query parameter" }));
                        response.ContentType = "application/json; charset=utf-8";
                        response.StatusCode = 400;
                        await response.OutputStream.WriteAsync(jsonBytes);
                    }
                    return;
                }

                // POST /api/variables (JSON body: {"name":"xxx","value":"yyy","displayName":"...","description":"..."})
                if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
                {
                    string name = string.Empty;
                    string value = string.Empty;
                    string? disp = null;
                    string? desc = null;

                    if (request.HasEntityBody)
                    {
                        using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
                        string body = await reader.ReadToEndAsync();
                        body = body.Trim();

                        if (body.StartsWith("{") && body.EndsWith("}"))
                        {
                            try
                            {
                                using var doc = JsonDocument.Parse(body);
                                if (doc.RootElement.TryGetProperty("name", out var nameProp))
                                {
                                    name = nameProp.GetString() ?? string.Empty;
                                }
                                if (doc.RootElement.TryGetProperty("value", out var valProp))
                                {
                                    value = valProp.GetString() ?? string.Empty;
                                }
                                else if (doc.RootElement.TryGetProperty("text", out var textProp))
                                {
                                    value = textProp.GetString() ?? string.Empty;
                                }
                                if (doc.RootElement.TryGetProperty("displayName", out var dispProp))
                                {
                                    disp = dispProp.GetString();
                                }
                                if (doc.RootElement.TryGetProperty("description", out var descProp))
                                {
                                    desc = descProp.GetString();
                                }
                            }
                            catch { }
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        var item = VariableService?.SetVariable(name, value, disp, desc);
                        StatusChanged?.Invoke($"外部变量更新: {{{name}}} = \"{value}\"");
                        var jsonBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { success = true, variable = item }));
                        response.ContentType = "application/json; charset=utf-8";
                        response.StatusCode = 200;
                        await response.OutputStream.WriteAsync(jsonBytes);
                    }
                    else
                    {
                        var jsonBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { success = false, error = "Invalid JSON or missing 'name'" }));
                        response.ContentType = "application/json; charset=utf-8";
                        response.StatusCode = 400;
                        await response.OutputStream.WriteAsync(jsonBytes);
                    }
                    return;
                }
            }

            // ----------------------------------------------------
            // 2. 游戏内/外部避让控制: /api/chatbox/avoid, /api/chatbox/pause, /api/chatbox/resume
            // ----------------------------------------------------
            if (rawPath.StartsWith("/api/chatbox/avoid") || rawPath.StartsWith("/api/chatbox/pause"))
            {
                int seconds = 12;
                if (int.TryParse(request.QueryString["seconds"] ?? request.QueryString["sec"], out int sVal))
                {
                    seconds = sVal;
                }
                PersistentTextService?.PauseAvoidance(seconds);
                StatusChanged?.Invoke($"外部接口触发避让: {seconds} 秒");
                var jsonBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { success = true, action = "avoid", seconds }));
                response.ContentType = "application/json; charset=utf-8";
                response.StatusCode = 200;
                await response.OutputStream.WriteAsync(jsonBytes);
                return;
            }

            if (rawPath.StartsWith("/api/chatbox/resume"))
            {
                PersistentTextService?.ResumeAvoidance();
                StatusChanged?.Invoke("外部接口恢复常驻保活");
                var jsonBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { success = true, action = "resume" }));
                response.ContentType = "application/json; charset=utf-8";
                response.StatusCode = 200;
                await response.OutputStream.WriteAsync(jsonBytes);
                return;
            }

            // ----------------------------------------------------
            // 3. 原有通用消息发送路由 (/send, 纯文本, 等)
            // ----------------------------------------------------
            string receivedText = string.Empty;
            if (request.QueryString["text"] != null)
            {
                receivedText = request.QueryString["text"] ?? string.Empty;
            }
            else if (request.QueryString["msg"] != null)
            {
                receivedText = request.QueryString["msg"] ?? string.Empty;
            }
            else if (request.HasEntityBody)
            {
                using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
                string body = await reader.ReadToEndAsync();
                body = body.Trim();

                if (body.StartsWith("{") && body.EndsWith("}"))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(body);
                        if (doc.RootElement.TryGetProperty("text", out var textProp))
                        {
                            receivedText = textProp.GetString() ?? string.Empty;
                        }
                        else if (doc.RootElement.TryGetProperty("message", out var msgProp))
                        {
                            receivedText = msgProp.GetString() ?? string.Empty;
                        }
                    }
                    catch
                    {
                        receivedText = body;
                    }
                }
                else
                {
                    receivedText = body;
                }
            }

            receivedText = receivedText.Trim();

            if (!string.IsNullOrEmpty(receivedText))
            {
                MessageReceived?.Invoke(receivedText, "HTTP API");
                var responseBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { success = true, text = receivedText }));
                response.ContentType = "application/json; charset=utf-8";
                response.StatusCode = 200;
                await response.OutputStream.WriteAsync(responseBytes);
            }
            else
            {
                var responseBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { success = false, error = "Empty text" }));
                response.ContentType = "application/json; charset=utf-8";
                response.StatusCode = 400;
                await response.OutputStream.WriteAsync(responseBytes);
            }
        }
        catch { }
        finally
        {
            try { context.Response.Close(); } catch { }
        }
    }
}
