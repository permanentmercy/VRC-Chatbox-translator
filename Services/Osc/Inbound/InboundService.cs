using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace VrcChatboxDemo.Services;

/// <summary>
/// 开放外部应用程序接入服务 (支持本地 HTTP REST API 与 UDP OSC 协议双向接收外部文本)
/// </summary>
public partial class InboundService : IDisposable
{
    private HttpListener? _httpListener;
    private UdpClient? _udpClient;
    private CancellationTokenSource? _cts;
    private bool _isRunning = false;

    public int HttpPort { get; set; } = 9002;
    public int OscPort { get; set; } = 9001;
    public bool AutoSendToVrc { get; set; } = true;
    public bool IsRunning => _isRunning;

    public VariableService? VariableService { get; set; }
    public PersistentTextService? PersistentTextService { get; set; }

    public event Action<string, string>? MessageReceived;
    public event Action<string>? StatusChanged;

    public void Start(int httpPort = 9002, int oscPort = 9001)
    {
        Stop();

        HttpPort = httpPort;
        OscPort = oscPort;
        _cts = new CancellationTokenSource();

        // 两个监听器都可能因为端口被占用/URL 保留权限而启动失败。
        // 只有至少一个真正监听成功时才把服务标记为运行中，否则 UI 会显示
        // “已就绪”并把 InboundEnabled 持久化为 true，但实际没有任何端口在监听。
        bool httpOk = StartHttpServer(_cts.Token);
        bool udpOk = StartOscUdpServer(_cts.Token);
        _isRunning = httpOk || udpOk;

        if (!_isRunning)
        {
            try
            {
                _cts.Cancel();
                _cts.Dispose();
                _cts = null;
            }
            catch { }
            StatusChanged?.Invoke($"外部接口启动失败 (HTTP: {HttpPort}, UDP OSC: {OscPort})");
            return;
        }

        if (httpOk && udpOk)
        {
            StatusChanged?.Invoke($"外部接口监听已就绪 (HTTP: {HttpPort}, UDP OSC: {OscPort})");
        }
        else
        {
            StatusChanged?.Invoke(httpOk
                ? $"外部接口部分就绪：HTTP {HttpPort} 已监听，UDP OSC {OscPort} 启动失败"
                : $"外部接口部分就绪：UDP OSC {OscPort} 已监听，HTTP {HttpPort} 启动失败");
        }
    }

    public void Stop()
    {
        bool wasRunning = _isRunning;
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

        // Start() 内部会先 Stop() 做清理；若此时并没有在运行，不应再广播
        // “已关闭”，否则刚启动就会在状态栏刷一条误导性的关闭提示。
        if (wasRunning)
        {
            StatusChanged?.Invoke("外部接口监听已关闭");
        }
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}
