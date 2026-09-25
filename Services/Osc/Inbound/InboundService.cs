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

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}
