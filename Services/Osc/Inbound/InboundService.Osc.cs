using System;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VrcChatboxDemo.Services;

public partial class InboundService
{
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
}
