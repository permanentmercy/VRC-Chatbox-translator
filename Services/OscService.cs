using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace VrcChatboxDemo.Services;

public record OscLogItem(DateTime Timestamp, string Address, string Content, bool Success, string? Error = null);

public class OscService : IDisposable
{
    private UdpClient? _udpClient;
    private string _host = "127.0.0.1";
    private int _port = 9000;

    public string Host
    {
        get => _host;
        set
        {
            if (_host != value)
            {
                _host = value;
                ResetClient();
            }
        }
    }

    public int Port
    {
        get => _port;
        set
        {
            if (_port != value)
            {
                _port = value;
                ResetClient();
            }
        }
    }

    public event Action<OscLogItem>? MessageSent;

    private void ResetClient()
    {
        _udpClient?.Dispose();
        _udpClient = null;
    }

    private UdpClient GetClient()
    {
        if (_udpClient == null)
        {
            _udpClient = new UdpClient();
        }
        return _udpClient;
    }

    /// <summary>
    /// 发送文本消息到 VRChat Chatbox (/chatbox/input)
    /// </summary>
    /// <param name="text">要发送的文本，UTF-8编码，最大支持约144字符</param>
    /// <param name="direct">true: 直接显示在头顶气泡; false: 仅呼出输入键盘</param>
    /// <param name="playSound">true: 播放提示音; false: 静音</param>
    /// <param name="recordLog">是否在日志面板中记录此消息</param>
    public async Task<bool> SendChatboxMessageAsync(string text, bool direct = true, bool playSound = true, bool recordLog = true)
    {
        const string address = "/chatbox/input";
        byte[] packet = BuildChatboxInputPacket(address, text, direct, playSound);
        return await SendPacketAsync(address, $"文本: \"{text}\" (Direct={direct}, Sound={playSound})", packet, recordLog);
    }

    /// <summary>
    /// 发送打字中状态动画到 VRChat (/chatbox/typing)
    /// </summary>
    /// <param name="isTyping">true: 开启打字动画; false: 关闭打字动画</param>
    /// <param name="recordLog">是否在日志面板中记录此消息</param>
    public async Task<bool> SendTypingAsync(bool isTyping, bool recordLog = true)
    {
        const string address = "/chatbox/typing";
        byte[] packet = BuildTypingPacket(address, isTyping);
        return await SendPacketAsync(address, $"打字状态: {isTyping}", packet, recordLog);
    }

    /// <summary>
    /// 清空 VRChat 头顶气泡内容
    /// </summary>
    public async Task<bool> ClearChatboxAsync()
    {
        return await SendChatboxMessageAsync(string.Empty, direct: true, playSound: false, recordLog: true);
    }

    private async Task<bool> SendPacketAsync(string address, string contentDescription, byte[] packet, bool recordLog = true)
    {
        try
        {
            var client = GetClient();
            await client.SendAsync(packet, packet.Length, _host, _port);
            if (recordLog)
            {
                MessageSent?.Invoke(new OscLogItem(DateTime.Now, address, contentDescription, true));
            }
            return true;
        }
        catch (Exception ex)
        {
            if (recordLog)
            {
                MessageSent?.Invoke(new OscLogItem(DateTime.Now, address, contentDescription, false, ex.Message));
            }
            return false;
        }
    }

    /// <summary>
    /// 构造 /chatbox/input OSC 报文
    /// 格式: Address: "/chatbox/input", TypeTags: ",sTT" / ",sTF" / ",sFT" / ",sFF", Args: [text]
    /// </summary>
    public static byte[] BuildChatboxInputPacket(string address, string text, bool direct, bool playSound)
    {
        using var ms = new MemoryStream();

        // 1. Address
        WriteOscString(ms, address);

        // 2. Type tags: ,s[T|F][T|F]
        char directTag = direct ? 'T' : 'F';
        char soundTag = playSound ? 'T' : 'F';
        string typeTag = $",s{directTag}{soundTag}";
        WriteOscString(ms, typeTag);

        // 3. String argument (T and F have no argument payload in OSC 1.0)
        WriteOscString(ms, text ?? string.Empty);

        return ms.ToArray();
    }

    /// <summary>
    /// 构造 /chatbox/typing OSC 报文
    /// 格式: Address: "/chatbox/typing", TypeTags: ",T" / ",F", Args: none
    /// </summary>
    public static byte[] BuildTypingPacket(string address, bool isTyping)
    {
        using var ms = new MemoryStream();

        // 1. Address
        WriteOscString(ms, address);

        // 2. Type tags: ,T or ,F
        char typingTag = isTyping ? 'T' : 'F';
        string typeTag = $",{typingTag}";
        WriteOscString(ms, typeTag);

        return ms.ToArray();
    }

    /// <summary>
    /// 写入以 null 结尾并按 4 字节对齐补零的 OSC 字符串 (UTF-8)
    /// </summary>
    private static void WriteOscString(MemoryStream ms, string str)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(str);
        ms.Write(bytes, 0, bytes.Length);
        ms.WriteByte(0); // 至少一个 null 终止符

        // 填充至 4 字节倍数
        int remainder = (int)(ms.Position % 4);
        if (remainder != 0)
        {
            int pad = 4 - remainder;
            for (int i = 0; i < pad; i++)
            {
                ms.WriteByte(0);
            }
        }
    }

    public void Dispose()
    {
        _udpClient?.Dispose();
        _udpClient = null;
    }
}
