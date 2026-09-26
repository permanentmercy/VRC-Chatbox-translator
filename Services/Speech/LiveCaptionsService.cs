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

    public async Task<bool> RestartAsync(bool hideNativeWindow = true)
    {
        await _sessionLock.WaitAsync();
        try
        {
            _isRunning = false;
            RunningStateChanged?.Invoke(false);

            try
            {
                _workerCts?.Cancel();
                _workerCts?.Dispose();
                _workerCts = null;
            }
            catch { }

            // 终止旧的 LiveCaptions 进程实例，以便重新加载注册表中的 CaptionLanguage
            try
            {
                var procs = Process.GetProcessesByName("LiveCaptions");
                foreach (var p in procs)
                {
                    try { p.Kill(); } catch { }
                }
            }
            catch { }

            _hWnd = IntPtr.Zero;
            await Task.Delay(200);

            _workerCts = new CancellationTokenSource();
            var token = _workerCts.Token;

            _hWnd = await EnsureLiveCaptionsProcessAsync(token);
            if (_hWnd == IntPtr.Zero)
            {
                ErrorOccurred?.Invoke("重启 Windows 实时字幕失败，未找到窗口");
                return false;
            }

            if (hideNativeWindow)
            {
                HideNativeWindow();
            }

            _isRunning = true;
            RunningStateChanged?.Invoke(true);
            StatusChanged?.Invoke("Windows 11 实时字幕已完成重启并应用新语言");

            _ = Task.Run(() => PollingLoopAsync(token), token);
            return true;
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"重启 Windows 实时字幕异常: {ex.Message}");
            return false;
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

        bool isInitialBaseline = true;

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

                // 首帧基线校准：若首次连接到 Windows 实时字幕窗口时已有大段历史文本，
                // 绝不循环切句并发广播，而是将全部历史沉淀为基线，至多仅取最后 1 句触发提交
                if (isInitialBaseline)
                {
                    isInitialBaseline = false;
                    _lastFullText = fullText;
                    idleTimer.Restart();

                    AlignInitialBaseline(fullText);
                    await Task.Delay(40, ct);
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

                // 剩余部分不包含中间已确定的句子边界，作为当前正在连贯表达的单句假说
                if (!string.IsNullOrWhiteSpace(currentNewText))
                {
                    SpeechHypothesis?.Invoke(currentNewText);

                    // 智能语义完整性与停顿定稿判定：
                    // 英语环境下深度感知悬空词（连词/介词/助动词）与标点闭合，杜绝腰斩从句与把思考断成碎片；
                    // 中文环境下保持快速响应与平滑切分。
                    long elapsed = idleTimer.ElapsedMilliseconds;
                    if (ShouldCommitHypothesis(currentNewText, elapsed))
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

    private void AlignInitialBaseline(string fullText)
    {
        var allSentences = new List<string>();
        string tempText = fullText.Trim();

        while (true)
        {
            int boundaryIdx = FindSentenceBoundary(tempText);
            if (boundaryIdx < 0)
            {
                break;
            }

            string sentence = tempText.Substring(0, boundaryIdx + 1).Trim();
            tempText = tempText.Substring(boundaryIdx + 1).TrimStart(' ', '\t', '，', ',', '、', '；', ';', '。', '！', '？', '…', '.', '!', '?');

            if (ContainsMeaningfulContent(sentence))
            {
                allSentences.Add(sentence);
            }
        }

        if (allSentences.Count == 0)
        {
            if (ContainsMeaningfulContent(tempText))
            {
                SpeechHypothesis?.Invoke(tempText);
            }
            return;
        }

        // 将除最后一句之外的所有历史句子直接静默记入已消费记录（作为基线水印，杜绝重复切分）
        for (int i = 0; i < allSentences.Count - 1; i++)
        {
            RecordCommittedSentence(allSentences[i]);
        }

        // 仅取最后 1 句作为最新识别内容触发提交
        string lastSentence = allSentences[^1];
        RecordCommittedSentence(lastSentence);
        SpeechRecognized?.Invoke(lastSentence);
        StatusChanged?.Invoke($"已识别 (初始基线): {lastSentence}");

        // 如果在最后一句之后还有未闭合的短语，作为假说更新
        if (ContainsMeaningfulContent(tempText))
        {
            SpeechHypothesis?.Invoke(tempText);
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

    private static readonly HashSet<string> EnglishAbbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "mr.", "mrs.", "ms.", "dr.", "prof.", "sr.", "jr.", "vs.", "etc.", "e.g.", "i.e.", "st.", "inc.", "ltd.", "co.", "corp.", "approx.", "dept.", "est.", "fig.", "jan.", "feb.", "mar.", "apr.", "jun.", "jul.", "aug.", "sep.", "sept.", "oct.", "nov.", "dec.", "a.m.", "p.m.", "am.", "pm.", "u.s.", "u.k.", "e.u."
    };

    private static readonly HashSet<string> EnglishDanglingWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // 连词与引导词
        "and", "or", "but", "nor", "so", "yet", "because", "although", "though", "even",
        "if", "unless", "when", "whenever", "while", "where", "whereas", "since", "as",
        "that", "which", "who", "whom", "whose", "whether", "what", "whatever", "how",
        // 介词
        "to", "for", "with", "about", "of", "in", "on", "at", "from", "by", "into",
        "onto", "upon", "through", "between", "among", "without", "like", "than", "towards",
        // 助动词与系动词
        "is", "are", "am", "was", "were", "be", "been", "being",
        "have", "has", "had", "do", "does", "did",
        "will", "would", "shall", "should", "can", "could", "may", "might", "must",
        // 冠词与代词修饰
        "the", "a", "an", "this", "that", "these", "those", "my", "your", "his", "her", "its", "our", "their"
    };

    private static readonly HashSet<string> EnglishCommonShortResponses = new(StringComparer.OrdinalIgnoreCase)
    {
        "yes", "no", "yeah", "yep", "nope", "okay", "ok", "sure", "thanks", "thank you",
        "sorry", "hello", "hi", "hey", "goodbye", "bye", "bye bye", "got it", "all right",
        "alright", "of course", "never mind", "good morning", "good night", "see you"
    };

    private static List<string> ExtractEnglishWords(string text)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return list;

        var matches = System.Text.RegularExpressions.Regex.Matches(text, @"\b[a-zA-Z0-9'-]+\b");
        foreach (System.Text.RegularExpressions.Match m in matches)
        {
            list.Add(m.Value);
        }
        return list;
    }

    private static bool EndsWithDanglingEnglishWord(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        string trimmed = text.TrimEnd(' ', '\t', '，', ',', '、', '；', ';', '-', '—');
        var words = ExtractEnglishWords(trimmed);
        if (words.Count == 0) return false;

        string lastWord = words[^1];
        return EnglishDanglingWords.Contains(lastWord);
    }

    private static bool EndsWithTerminalPunctuation(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        string trimmed = text.TrimEnd();
        if (trimmed.Length == 0) return false;
        char last = trimmed[^1];
        if (last == '"' || last == '\'' || last == '”' || last == '’' || last == ')' || last == ']')
        {
            trimmed = trimmed.Substring(0, trimmed.Length - 1).TrimEnd();
            if (trimmed.Length == 0) return false;
            last = trimmed[^1];
        }
        return last == '。' || last == '！' || last == '？' || last == '…' || last == '.' || last == '!' || last == '?';
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

                // 1. 忽略大小写查找（解决英文实时字幕 ASR 动态调整首字母大小写的匹配丢失问题）
                int idx = fullText.LastIndexOf(sent, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    return fullText.Substring(idx + sent.Length);
                }

                // 2. 备用标点与空格容差匹配（不区分大小写）
                string fuzzySent = TrimTrailingPunctuation(sent);
                if (fuzzySent.Length >= 2)
                {
                    int fIdx = fullText.LastIndexOf(fuzzySent, StringComparison.OrdinalIgnoreCase);
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

        int lastIdx = fullText.LastIndexOf(_lastCommittedSentence, StringComparison.OrdinalIgnoreCase);
        if (lastIdx >= 0)
        {
            return fullText.Substring(lastIdx + _lastCommittedSentence.Length);
        }

        // 3. 词级后置锚定与防重复保护（当英文 ASR 回溯修改了已提交句子的中间词或标点时）：
        // 绝不盲目返回整段 fullText 重复切句与提交，而是寻找最近已提交句子的末尾词向后定位
        string safeFallback = FindRemainingTextByAnchor(fullText);
        return safeFallback;
    }

    private string FindRemainingTextByAnchor(string fullText)
    {
        lock (_recentCommittedSentences)
        {
            for (int i = _recentCommittedSentences.Count - 1; i >= 0; i--)
            {
                string sent = _recentCommittedSentences[i];
                if (string.IsNullOrWhiteSpace(sent)) continue;

                // 尝试提取该句的最后 2~3 个词作为锚点
                var words = sent.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (words.Length >= 2)
                {
                    string anchor = words[^2] + " " + words[^1];
                    anchor = TrimTrailingPunctuation(anchor);
                    if (anchor.Length >= 4)
                    {
                        int aIdx = fullText.LastIndexOf(anchor, StringComparison.OrdinalIgnoreCase);
                        if (aIdx >= 0)
                        {
                            int cutStart = aIdx + anchor.Length;
                            while (cutStart < fullText.Length && IsDelimiterOrPunctuation(fullText[cutStart]))
                            {
                                cutStart++;
                            }
                            return fullText.Substring(cutStart);
                        }
                    }
                }
            }

            // 若已有提交历史且全文长度未明显增长，绝不重复返回整段文本引起刷屏
            if (_recentCommittedSentences.Count > 0)
            {
                return string.Empty;
            }
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

    private static bool ContainsCjk(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (char c in text)
        {
            if (c >= 0x4E00 && c <= 0x9FFF) return true;
            if (c >= 0x3400 && c <= 0x4DBF) return true;
            if (c >= 0x3040 && c <= 0x30FF) return true;
            if (c >= 0xAC00 && c <= 0xD7AF) return true;
        }
        return false;
    }

    private static int FindSentenceBoundary(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return -1;
        bool hasCjk = ContainsCjk(text);

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (IsTerminalDelimiter(c))
            {
                // 1. 浮点数/版本号/小数点保护：如 3.14, 1.0, 不作为断句符
                if (c == '.' && i > 0 && i + 1 < text.Length && char.IsDigit(text[i - 1]) && char.IsDigit(text[i + 1]))
                {
                    continue;
                }

                if (!hasCjk)
                {
                    // 2. 西文环境下：若终结符处于文本末尾（后面没有任何有效字母或数字），
                    // 绝不在流式接收中毫秒级秒切！交给 ShouldCommitHypothesis 在静默 750ms 后平滑定稿，
                    // 从而彻底防止说话人还在连贯说话时被半途吐出的临时点号腰斩。
                    bool isTrailing = true;
                    for (int k = i + 1; k < text.Length; k++)
                    {
                        if (char.IsLetterOrDigit(text[k]))
                        {
                            isTrailing = false;
                            break;
                        }
                    }
                    if (isTrailing)
                    {
                        continue;
                    }

                    // 3. 终结符在文本中间：检查其后是否由空格、换行或引号隔开
                    bool followedBySpaceOrQuote = char.IsWhiteSpace(text[i + 1]) ||
                                                  text[i + 1] == '"' || text[i + 1] == '\'' ||
                                                  text[i + 1] == '”' || text[i + 1] == '’' ||
                                                  text[i + 1] == ')' || text[i + 1] == ']';
                    if (!followedBySpaceOrQuote)
                    {
                        continue;
                    }

                    // 4. 常见英语缩写词保护：提取该句号结尾的词，如 Mr., Dr., etc., vs., e.g.
                    if (c == '.')
                    {
                        int wordStart = i - 1;
                        while (wordStart > 0 && !char.IsWhiteSpace(text[wordStart - 1]) && text[wordStart - 1] != '(' && text[wordStart - 1] != '[')
                        {
                            wordStart--;
                        }
                        string token = text.Substring(wordStart, i - wordStart + 1).Trim();
                        if (EnglishAbbreviations.Contains(token))
                        {
                            continue;
                        }

                        // 单个大写字母缩写保护：如 A. 或 John F. Kennedy
                        if (token.Length == 2 && char.IsUpper(token[0]))
                        {
                            continue;
                        }
                    }

                    // 5. 核心：西文确定性新句判定（Strong Boundary Check）
                    // 终结符之后跳过空格和引号，下一个词的第一个字母必须是大写字母（或数字/引号）！
                    // 若下一个字母是小写字母（如 ASR 误打点号），绝不拆分！
                    int nextCharIdx = i + 1;
                    while (nextCharIdx < text.Length && (char.IsWhiteSpace(text[nextCharIdx]) ||
                                                         text[nextCharIdx] == '"' || text[nextCharIdx] == '\'' ||
                                                         text[nextCharIdx] == '“' || text[nextCharIdx] == '‘'))
                    {
                        nextCharIdx++;
                    }

                    if (nextCharIdx < text.Length)
                    {
                        char nextC = text[nextCharIdx];
                        if (char.IsLetter(nextC) && !char.IsUpper(nextC))
                        {
                            continue;
                        }
                    }
                }

                // 6. 最小有效长度保护：终结符前必须至少有有效内容（中文 2 字，英文至少 3 字符）
                string prefix = text.Substring(0, i);
                int minChars = hasCjk ? 2 : 3;
                if (GetMeaningfulContentLength(prefix) < minChars)
                {
                    continue;
                }

                int endIdx = i;
                while (endIdx + 1 < text.Length && (IsTerminalDelimiter(text[endIdx + 1]) ||
                                                    IsCommaDelimiter(text[endIdx + 1]) ||
                                                    text[endIdx + 1] == '"' || text[endIdx + 1] == '\'' ||
                                                    text[endIdx + 1] == '”' || text[endIdx + 1] == '’' ||
                                                    text[endIdx + 1] == ')' || text[endIdx + 1] == ']'))
                {
                    endIdx++;
                }
                return endIdx;
            }

            // 7. 超长句逗号兜底保护：
            // 中文单句达到 40 个字符遇逗号切分；
            // 英文单句绝不在正常逗号处切断（彻底杜绝腰斩从句与复合句），仅当段落达到 220 字符极大值且遇逗号时才兜底切分
            int commaThreshold = hasCjk ? 40 : 220;
            if (IsCommaDelimiter(c) && i >= commaThreshold)
            {
                int endIdx = i;
                while (endIdx + 1 < text.Length && (IsCommaDelimiter(text[endIdx + 1]) || IsTerminalDelimiter(text[endIdx + 1])))
                {
                    endIdx++;
                }
                return endIdx;
            }
        }

        return -1;
    }

    private static bool ShouldCommitHypothesis(string text, long elapsedMs)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        bool hasCjk = ContainsCjk(text);

        if (hasCjk)
        {
            // 中文/CJK 环境：
            // 1. 若末尾带终结符（。！？…），只要静默 >= 600ms 立即定稿；
            // 2. 普通无终结符内容，有效字数 >= 3 且静默 >= 1600ms 定稿；
            // 3. 极短字（1-2字）需静默 >= 2500ms 彻底无后续才定稿
            if (EndsWithTerminalPunctuation(text))
            {
                return elapsedMs >= 600;
            }

            int cjkLen = GetMeaningfulContentLength(text);
            if (cjkLen >= 3 && elapsedMs >= 1600) return true;
            if (cjkLen >= 1 && elapsedMs >= 2500) return true;
            return false;
        }
        else
        {
            // 英文/西文环境：
            // 1. 若末尾带有句号/感叹号/问号（且不是缩写词结尾），说明本句已完整打出标点：
            //    仅需静默停顿 >= 750ms（既不会抢在连贯说话前腰斩，又保证说完后极速上屏发送）
            if (EndsWithTerminalPunctuation(text))
            {
                string trimmed = text.TrimEnd(' ', '\t', '"', '\'', '”', '’', ')', ']');
                var words = ExtractEnglishWords(trimmed);
                if (words.Count > 0 && EnglishAbbreviations.Contains(words[^1] + "."))
                {
                    // 末尾是缩写词，不作为已闭合的句子
                }
                else
                {
                    return elapsedMs >= 750;
                }
            }

            var englishWords = ExtractEnglishWords(text);
            int wordCount = englishWords.Count;
            if (wordCount == 0) return false;

            // 2. 检查末尾是否处于悬空未完成态（以 and, but, because, to, for, with, is, are 等结尾，或以逗号结尾）
            bool isDangling = EndsWithDanglingEnglishWord(text) || text.TrimEnd().EndsWith(',') || text.TrimEnd().EndsWith(';');
            if (isDangling)
            {
                // 悬空状态表明说话人正在连贯组织后续词汇，给予充足等待时间（>= 3200ms），绝不随意腰斩！
                return elapsedMs >= 3200;
            }

            // 3. 极短回复词（1~2词）：
            //    如果是常见独立短语（Yes, No, Thank you, All right, Sure 等），静默 >= 1300ms 即可定稿；
            //    如果是普通零散词，需要静默 >= 2800ms 彻底无后续才定稿，避免把句首单词提前发出去
            if (wordCount <= 2)
            {
                string cleanPhrase = string.Join(" ", englishWords).ToLowerInvariant();
                if (EnglishCommonShortResponses.Contains(cleanPhrase) || (wordCount == 1 && EnglishCommonShortResponses.Contains(englishWords[0])))
                {
                    return elapsedMs >= 1300;
                }
                return elapsedMs >= 2800;
            }

            // 4. 正常多词表达（>= 3个单词且不悬空）：
            //    用户说完一段语义相对完整的语句，静默 >= 1900ms 定稿提交
            return elapsedMs >= 1900;
        }
    }

    private static int GetMeaningfulContentLength(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        int count = 0;
        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c)) count++;
        }
        return count;
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
        // 1. 若换行符两侧存在西文字符、数字或西文标点，且任一侧不是空格，则替换为单个空格，防止西文单词或标点后粘连（如 "world.\nToday" -> "world. Today"）
        // 2. 纯中文字符之间的换行符直接剔除，保持中文连贯
        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\n')
            {
                char prev = (i > 0) ? text[i - 1] : '\0';
                char next = (i + 1 < text.Length) ? text[i + 1] : '\0';

                bool prevIsLatin = IsAsciiAlphaNumeric(prev) || IsEnglishPunctuation(prev);
                bool nextIsLatin = IsAsciiAlphaNumeric(next) || IsEnglishPunctuation(next);

                if ((prevIsLatin || nextIsLatin) && prev != ' ' && next != ' ')
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

        // 确保西文标点（, . ! ? ; :）后面若直接紧跟西文字母，自动补充一个空格，保障词级解析与大写检测精准
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"([a-zA-Z0-9])([,.;:!?])([a-zA-Z])", "$1$2 $3");

        while (cleaned.Contains("  "))
        {
            cleaned = cleaned.Replace("  ", " ");
        }

        return cleaned;
    }

    private static bool IsEnglishPunctuation(char c)
    {
        return c == '.' || c == ',' || c == '!' || c == '?' || c == ';' || c == ':' || c == '"' || c == '\'' || c == '-' || c == ')';
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
