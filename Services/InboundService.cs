using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VrcChatboxDemo.Services;

/// <summary>
/// 开放外部应用程序接入服务 (支持本地 HTTP REST API 与 UDP OSC 协议双向接收外部文本)
/// </summary>
public class InboundService : IDisposable
{
    private HttpListener? _httpListener;
    private UdpClient? _udpClient;
    private CancellationTokenSource? _cts;
    private bool _isRunning = false;

    public int HttpPort { get; set; } = 9002;
    public int OscPort { get; set; } = 9001;
    public bool AutoSendToVrc { get; set; } = true;
    public bool IsRunning => _isRunning;

    public event Action<string, string>? MessageReceived;
    public event Action<string>? StatusChanged;

    public void Start(int httpPort = 9002, int oscPort = 9001)
    {
        Stop();

        HttpPort = httpPort;
        OscPort = oscPort;
        _cts = new CancellationTokenSource();
        _isRunning = true;

        StartHttpServer(_cts.Token);
        StartOscUdpServer(_cts.Token);

        StatusChanged?.Invoke($"外部接口监听已就绪 (HTTP: {HttpPort}, UDP OSC: {OscPort})");
    }

    public void Stop()
    {
        _isRunning = false;
        try
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }
        catch { }

        try
        {
            _httpListener?.Stop();
            _httpListener?.Close();
            _httpListener = null;
        }
        catch { }

        try
        {
            _udpClient?.Close();
            _udpClient?.Dispose();
            _udpClient = null;
        }
        catch { }

        StatusChanged?.Invoke("外部接口监听已关闭");
    }

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
            string receivedText = string.Empty;
            var request = context.Request;

            // 1. 优先从 QueryString 读取: /send?text=... 或 ?msg=...
            if (request.QueryString["text"] != null)
            {
                receivedText = request.QueryString["text"] ?? string.Empty;
            }
            else if (request.QueryString["msg"] != null)
            {
                receivedText = request.QueryString["msg"] ?? string.Empty;
            }
            // 2. 从 POST Body 读取 (支持 JSON {"text":"..."} 或纯文本)
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

            // 跨域支持 (CORS)
            context.Response.Headers.Add("Access-Control-Allow-Origin", "*");
            context.Response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            context.Response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

            if (request.HttpMethod.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = 200;
                context.Response.Close();
                return;
            }

            if (!string.IsNullOrEmpty(receivedText))
            {
                MessageReceived?.Invoke(receivedText, "HTTP API");
                var responseBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { success = true, text = receivedText }));
                context.Response.ContentType = "application/json; charset=utf-8";
                context.Response.StatusCode = 200;
                await context.Response.OutputStream.WriteAsync(responseBytes);
            }
            else
            {
                var responseBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { success = false, error = "Empty text" }));
                context.Response.ContentType = "application/json; charset=utf-8";
                context.Response.StatusCode = 400;
                await context.Response.OutputStream.WriteAsync(responseBytes);
            }
        }
        catch { }
        finally
        {
            try { context.Response.Close(); } catch { }
        }
    }

    private void StartOscUdpServer(CancellationToken ct)
    {
        try
        {
            _udpClient = new UdpClient(OscPort);

            _ = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested && _udpClient != null)
                {
                    try
                    {
                        var result = await _udpClient.ReceiveAsync(ct);
                        string parsed = ParseOscOrPlainMessage(result.Buffer);
                        if (!string.IsNullOrWhiteSpace(parsed))
                        {
                            MessageReceived?.Invoke(parsed, $"UDP OSC ({result.RemoteEndPoint.Port})");
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch (ObjectDisposedException) { break; }
                    catch { }
                }
            }, ct);
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"UDP OSC 外部接口启动异常 ({OscPort}): {ex.Message}");
        }
    }

    private string ParseOscOrPlainMessage(byte[] data)
    {
        if (data == null || data.Length == 0) return string.Empty;

        // 如果是标准的 OSC 数据包 (以 '/' 开头)
        if (data[0] == '/')
        {
            try
            {
                int addressEnd = Array.IndexOf(data, (byte)0);
                if (addressEnd <= 0) return string.Empty;

                string address = Encoding.ASCII.GetString(data, 0, addressEnd);

                // 核心安全过滤：VRChat 内部广播的 Avatar 参数、追踪及控制遥测数据一律直接丢弃，严禁混入文字聊天
                if (address.StartsWith("/avatar/", StringComparison.OrdinalIgnoreCase) ||
                    address.StartsWith("/tracking/", StringComparison.OrdinalIgnoreCase) ||
                    address.StartsWith("/input/", StringComparison.OrdinalIgnoreCase))
                {
                    return string.Empty;
                }

                // OSC 对齐到 4 字节
                int tagIndex = (addressEnd + 4) & ~3;

                if (tagIndex < data.Length && data[tagIndex] == ',')
                {
                    int tagEnd = Array.IndexOf(data, (byte)0, tagIndex);
                    if (tagEnd > 0)
                    {
                        string typeTags = Encoding.ASCII.GetString(data, tagIndex, tagEnd - tagIndex);
                        int dataIndex = (tagEnd + 4) & ~3;

                        for (int i = 1; i < typeTags.Length; i++)
                        {
                            if (typeTags[i] == 's')
                            {
                                int strEnd = Array.IndexOf(data, (byte)0, dataIndex);
                                if (strEnd > dataIndex)
                                {
                                    string content = Encoding.UTF8.GetString(data, dataIndex, strEnd - dataIndex);
                                    if (!string.IsNullOrWhiteSpace(content) &&
                                        !content.StartsWith("/avatar/", StringComparison.OrdinalIgnoreCase) &&
                                        !content.StartsWith("/tracking/", StringComparison.OrdinalIgnoreCase) &&
                                        !content.StartsWith("/input/", StringComparison.OrdinalIgnoreCase))
                                    {
                                        return content;
                                    }
                                }
                                break;
                            }
                            else if (typeTags[i] == 'i' || typeTags[i] == 'f')
                            {
                                dataIndex += 4;
                            }
                            else if (typeTags[i] == 'T' || typeTags[i] == 'F' || typeTags[i] == 'N')
                            {
                                // 布尔或空类型无数据体
                            }
                            else if (typeTags[i] == 'd')
                            {
                                dataIndex += 8;
                            }
                        }
                    }
                }

                // 重点防护：凡是以 '/' 开头的 OSC 协议包，若没有解析出字符串内容，绝对不允许回退解码为纯文本！
                // 否则会将 OSC 路径地址本身（如 /avatar/parameters/...）当成聊天文本发送出去
                return string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        // 仅在非 '/' 开头的数据流时，才作为普通 UTF-8 纯文本回退解析
        try
        {
            string plain = Encoding.UTF8.GetString(data).Trim('\0', ' ', '\r', '\n');
            if (plain.StartsWith("/avatar/", StringComparison.OrdinalIgnoreCase) ||
                plain.StartsWith("/tracking/", StringComparison.OrdinalIgnoreCase) ||
                plain.StartsWith("/input/", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }
            return plain;
        }
        catch
        {
            return string.Empty;
        }
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}
