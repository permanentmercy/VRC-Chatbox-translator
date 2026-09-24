using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Interop.UIAutomationClient;

namespace VrcChatboxDemo.Services;

/// <summary>
/// Windows 11 原生实时字幕 (LiveCaptions.exe) 联动服务
/// 参考开源项目 LiveCaptions-Translator 原理：
/// 1. 唤起系统底层 LiveCaptions 进程并隐藏其原生窗口；
/// 2. 使用 UIAutomation 高频读取 CaptionsTextBlock 的实时文字；
/// 3. 支持极速流式假说输出 (Hypothesis) 与智能断句提交 (Recognized)；
/// 4. 0 显存占用、极低系统资源开销，与 VRChat 完美共存不掉帧。
/// </summary>
public partial class LiveCaptionsService : IDisposable
{
    private IntPtr _hWnd = IntPtr.Zero;
    private bool _isRunning;
    private CancellationTokenSource? _workerCts;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);

    private static readonly char[] TerminalDelimiters = new[] { '。', '！', '？', '…', '.', '!', '?' };
    private static readonly char[] CommaDelimiters = new[] { '，', ',', '、', '；', ';' };
    private string _lastCommittedSentence = string.Empty;
    private string _lastFullText = string.Empty;
    private readonly List<string> _recentCommittedSentences = new();

    public void ResetHistory()
    {
        lock (_recentCommittedSentences)
        {
            _recentCommittedSentences.Clear();
            if (!string.IsNullOrEmpty(_lastFullText))
            {
                _lastCommittedSentence = _lastFullText;
                _recentCommittedSentences.Add(_lastFullText);
            }
            else
            {
                _lastCommittedSentence = string.Empty;
            }
        }
    }

    public bool IsRunning => _isRunning;

    public event Action<string>? SpeechRecognized;
    public event Action<string>? SpeechHypothesis;
    public event Action<string>? StatusChanged;
    public event Action<string>? ErrorOccurred;
    public event Action<bool>? RunningStateChanged;

    public async Task<bool> StartAsync(bool hideNativeWindow = true)
    {
        await _sessionLock.WaitAsync();
        try
        {
            if (_isRunning) return true;

            _workerCts = new CancellationTokenSource();
            var token = _workerCts.Token;

            _hWnd = await EnsureLiveCaptionsProcessAsync(token);
            if (_hWnd == IntPtr.Zero)
            {
                ErrorOccurred?.Invoke("未找到 Windows 实时字幕窗口。请检查系统版本是否为 Windows 11 22H2 以上。");
                return false;
            }

            if (hideNativeWindow)
            {
                HideNativeWindow();
            }

            _isRunning = true;
            RunningStateChanged?.Invoke(true);
            StatusChanged?.Invoke("Windows 11 原生实时字幕监听中 (0 显存占用)...");

            _ = Task.Run(() => PollingLoopAsync(token), token);
            return true;
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"启动实时字幕监听失败: {ex.Message}");
            return false;
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    public async Task StopAsync()
    {
        await _sessionLock.WaitAsync();
        try
        {
            if (!_isRunning) return;

            _isRunning = false;
            RunningStateChanged?.Invoke(false);

            try
            {
                _workerCts?.Cancel();
                _workerCts?.Dispose();
                _workerCts = null;
            }
            catch { }

            StatusChanged?.Invoke("Windows 实时字幕监听已停止");
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private async Task PollingLoopAsync(CancellationToken ct)
    {
        CUIAutomation? uia = null;
        IUIAutomationElement? captionsTextBlock = null;

        var idleTimer = Stopwatch.StartNew();
        _lastCommittedSentence = string.Empty;
        _lastFullText = string.Empty;
        lock (_recentCommittedSentences)
        {
            _recentCommittedSentences.Clear();
        }

        while (!ct.IsCancellationRequested && _isRunning)
        {
            try
            {
                if (_hWnd == IntPtr.Zero || !IsWindow(_hWnd))
                {
                    _hWnd = FindLiveCaptionsWindow();
                    if (_hWnd == IntPtr.Zero)
                    {
                        captionsTextBlock = null;
                        await Task.Delay(1000, ct);
                        continue;
                    }
                }

                if (uia == null)
                {
                    uia = new CUIAutomation();
                }

                if (captionsTextBlock == null)
                {
                    var windowElement = uia.ElementFromHandle(_hWnd);
                    if (windowElement != null)
                    {
                        var condition = uia.CreatePropertyCondition(UIA_PropertyIds.UIA_AutomationIdPropertyId, "CaptionsTextBlock");
                        captionsTextBlock = windowElement.FindFirst(TreeScope.TreeScope_Descendants, condition);
                    }

                    if (captionsTextBlock == null)
                    {
                        await Task.Delay(300, ct);
                        continue;
                    }
                }

                string fullText = string.Empty;
                try
                {
                    fullText = captionsTextBlock.CurrentName ?? string.Empty;
                }
                catch
                {
                    captionsTextBlock = null;
                    continue;
                }

                fullText = CleanCaptionText(fullText);

                if (!string.Equals(fullText, _lastFullText, StringComparison.Ordinal))
                {
                    _lastFullText = fullText;
                    idleTimer.Restart();
                }

                if (string.IsNullOrWhiteSpace(fullText))
                {
                    await Task.Delay(35, ct);
                    continue;
                }

                // 提取尚未提交的全新文本段（具备防滚动回弹与模糊标点匹配）
                string currentNewText = ExtractNewText(fullText);
                currentNewText = currentNewText.TrimStart(' ', '\t', '，', ',', '、', '；', ';');

                if (string.IsNullOrWhiteSpace(currentNewText))
                {
                    await Task.Delay(35, ct);
                    continue;
                }

                // 循环切分已完成的句子：终结符（句号/感叹号/问号）立即切分，逗号每达到 2 个切为一句
                while (true)
                {
                    int boundaryIdx = FindSentenceBoundary(currentNewText);
                    if (boundaryIdx < 0)
                    {
                        break;
                    }

                    string sentenceToCommit = currentNewText.Substring(0, boundaryIdx + 1).Trim();
                    currentNewText = currentNewText.Substring(boundaryIdx + 1).TrimStart(' ', '\t', '，', ',', '、', '；', ';', '。', '！', '？', '…', '.', '!', '?');

                    if (ContainsMeaningfulContent(sentenceToCommit))
                    {
                        RecordCommittedSentence(sentenceToCommit);
                        SpeechRecognized?.Invoke(sentenceToCommit);
                        StatusChanged?.Invoke($"已识别: {sentenceToCommit}");
                        idleTimer.Restart();
                    }
                }

                // 剩余部分不包含任何终结符，仅作为当前正在说的单句假说
                if (!string.IsNullOrWhiteSpace(currentNewText))
                {
                    SpeechHypothesis?.Invoke(currentNewText);

                    // 如果说话停顿超过 750ms，且字数达到一定长度，主动断句提交
                    if (idleTimer.ElapsedMilliseconds >= 750 && currentNewText.Length >= 2)
                    {
                        string sentenceToCommit = currentNewText.Trim();
                        if (ContainsMeaningfulContent(sentenceToCommit))
                        {
                            RecordCommittedSentence(sentenceToCommit);
                            SpeechRecognized?.Invoke(sentenceToCommit);
                            StatusChanged?.Invoke($"已识别: {sentenceToCommit}");
                            currentNewText = string.Empty;
                            idleTimer.Restart();
                        }
                    }
                }

                await Task.Delay(40, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LiveCaptions] Error in loop: {ex.Message}");
                captionsTextBlock = null;
                await Task.Delay(500, ct);
            }
        }
    }

    private void RecordCommittedSentence(string sentence)
    {
        _lastCommittedSentence = sentence;
        lock (_recentCommittedSentences)
        {
            _recentCommittedSentences.Add(sentence);
            while (_recentCommittedSentences.Count > 15)
            {
                _recentCommittedSentences.RemoveAt(0);
            }
        }
    }

    private string ExtractNewText(string fullText)
    {
        if (string.IsNullOrWhiteSpace(fullText)) return string.Empty;

        lock (_recentCommittedSentences)
        {
            for (int i = _recentCommittedSentences.Count - 1; i >= 0; i--)
            {
                string sent = _recentCommittedSentences[i];
                if (string.IsNullOrWhiteSpace(sent)) continue;

                int idx = fullText.LastIndexOf(sent, StringComparison.Ordinal);
                if (idx >= 0)
                {
                    return fullText.Substring(idx + sent.Length);
                }

                // 备用标点容差匹配
                string fuzzySent = TrimTrailingPunctuation(sent);
                if (fuzzySent.Length >= 2)
                {
                    int fIdx = fullText.LastIndexOf(fuzzySent, StringComparison.Ordinal);
                    if (fIdx >= 0)
                    {
                        int cutStart = fIdx + fuzzySent.Length;
                        while (cutStart < fullText.Length && IsDelimiterOrPunctuation(fullText[cutStart]))
                        {
                            cutStart++;
                        }
                        return fullText.Substring(cutStart);
                    }
                }
            }
        }

        if (string.IsNullOrEmpty(_lastCommittedSentence))
        {
            return fullText;
        }

        int lastIdx = fullText.LastIndexOf(_lastCommittedSentence, StringComparison.Ordinal);
        if (lastIdx >= 0)
        {
            return fullText.Substring(lastIdx + _lastCommittedSentence.Length);
        }

        return fullText;
    }

    private static string TrimTrailingPunctuation(string s)
    {
        return s.TrimEnd(' ', '\t', '。', '！', '？', '…', '.', '!', '?', '，', ',', '、', '；', ';');
    }

    private static bool IsTerminalDelimiter(char c)
    {
        for (int i = 0; i < TerminalDelimiters.Length; i++)
        {
            if (TerminalDelimiters[i] == c) return true;
        }
        return false;
    }

    private static bool IsCommaDelimiter(char c)
    {
        for (int i = 0; i < CommaDelimiters.Length; i++)
        {
            if (CommaDelimiters[i] == c) return true;
        }
        return false;
    }

    private static int FindSentenceBoundary(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return -1;

        int commaCount = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (IsTerminalDelimiter(c))
            {
                int endIdx = i;
                while (endIdx + 1 < text.Length && (IsTerminalDelimiter(text[endIdx + 1]) || IsCommaDelimiter(text[endIdx + 1])))
                {
                    endIdx++;
                }
                return endIdx;
            }

            if (IsCommaDelimiter(c))
            {
                commaCount++;
                if (commaCount >= 2)
                {
                    int endIdx = i;
                    while (endIdx + 1 < text.Length && (IsCommaDelimiter(text[endIdx + 1]) || IsTerminalDelimiter(text[endIdx + 1])))
                    {
                        endIdx++;
                    }
                    return endIdx;
                }
            }
        }

        return -1;
    }

    private static bool IsDelimiterOrPunctuation(char c)
    {
        return c == '。' || c == '！' || c == '？' || c == '…' ||
               c == '.' || c == '!' || c == '?' || c == ' ' ||
               c == '，' || c == ',' || c == '、' || c == '；' || c == ';';
    }

    private static bool ContainsMeaningfulContent(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c)) return true;
            if (c >= 0x4E00 && c <= 0x9FFF) return true;
            if (c >= 0x3400 && c <= 0x4DBF) return true;
            if (c >= 0x3040 && c <= 0x30FF) return true;
            if (c >= 0xAC00 && c <= 0xD7AF) return true;
        }
        return false;
    }

    private static string CleanCaptionText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        // 统一规范化换行
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');

        // 过滤从字幕中读取到的所有换行符：
        // 1. 若换行符两侧均为西文字符/数字，插入单个空格防止英文单词粘连
        // 2. 其余情况（如中文字符、标点前后）直接剔除换行符，保持语句完整连贯，杜绝突兀换行
        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\n')
            {
                char prev = (i > 0) ? text[i - 1] : '\0';
                char next = (i + 1 < text.Length) ? text[i + 1] : '\0';

                if (IsAsciiAlphaNumeric(prev) && IsAsciiAlphaNumeric(next))
                {
                    sb.Append(' ');
                }
            }
            else
            {
                sb.Append(c);
            }
        }

        string cleaned = sb.ToString().Trim();
        while (cleaned.Contains("  "))
        {
            cleaned = cleaned.Replace("  ", " ");
        }

        return cleaned;
    }

    private static bool IsAsciiAlphaNumeric(char c)
    {
        return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');
    }

    public void Dispose()
    {
        _ = StopAsync();
        _sessionLock.Dispose();
        GC.SuppressFinalize(this);
    }
}
