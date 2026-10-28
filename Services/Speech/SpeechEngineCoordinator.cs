using System;
using System.Threading;
using System.Threading.Tasks;

namespace VrcChatboxDemo.Services.Speech;

/// <summary>
/// 一个可启停的语音引擎抽象。把 Whisper 与 LiveCaptions 的差异（各自的启动参数、
/// 运行状态字段）挡在适配器里，协调器只关心「启停 + 是否在跑」。
/// </summary>
public interface ISpeechEngineControl
{
    /// <summary>引擎标识，与配置里的 SpeechEngine 取值一致。</summary>
    string Name { get; }

    /// <summary>引擎当前是否真的在运行。</summary>
    bool IsRunning { get; }

    /// <summary>启动。启动参数由适配器自己从配置读取。</summary>
    Task<bool> StartAsync();

    /// <summary>停止（可重复调用）。</summary>
    Task StopAsync();
}

/// <summary>
/// 协调器需要的少量宿主事实与回调。抽成接口后，协调器不依赖 SettingsService、
/// 也不依赖 WinUI 的 DispatcherQueue，可以直接用替身测试。
/// </summary>
public interface ISpeechEngineContext
{
    /// <summary>用户当前选择的引擎名。</summary>
    string Engine { get; }

    /// <summary>用户期望的开关状态（意图，不是实际运行状态）。</summary>
    bool Enabled { get; set; }

    /// <summary>通知界面刷新（显示选项变化）。</summary>
    void NotifyDisplaySettingsChanged();

    /// <summary>通知界面「开关最终状态」；实现方负责派发到 UI 线程并读取最新值。</summary>
    void NotifyRecognitionStateChanged();

    /// <summary>写一条状态文本（状态栏 + 日志）。</summary>
    void SetStatus(string message);
}

/// <summary>
/// 语音引擎启停 / 切换的状态机。
///
/// 从 SettingsService 拆出来的原因：这里全是并发与回滚逻辑（快速拨开关、切引擎、
/// 启动失败回滚、迟到的失败不能覆盖新意图），与配置属性、字幕管线混在一起时
/// 既无法单测也无法安全修改。现在它只依赖两个引擎抽象 + 一个上下文接口。
/// </summary>
public sealed class SpeechEngineCoordinator
{
    private readonly ISpeechEngineContext _context;
    private readonly ISpeechEngineControl _whisper;
    private readonly ISpeechEngineControl _liveCaptions;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private long _version;
    private int _applying;

    public SpeechEngineCoordinator(
        ISpeechEngineContext context,
        ISpeechEngineControl whisper,
        ISpeechEngineControl liveCaptions)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _whisper = whisper ?? throw new ArgumentNullException(nameof(whisper));
        _liveCaptions = liveCaptions ?? throw new ArgumentNullException(nameof(liveCaptions));
    }

    /// <summary>当前意图版本号；每次用户改开关/切引擎都会递增。</summary>
    public long Version => Volatile.Read(ref _version);

    /// <summary>
    /// 用户意图发生变化时调用。递增版本号会让正在执行的 Apply 重新读取意图，
    /// 从而丢弃过期的启动结果。
    /// </summary>
    public void InvalidateIntent() => Interlocked.Increment(ref _version);

    /// <summary>
    /// 引擎上报运行状态变化（启动成功/失败、后台异常停止）。
    /// Apply 期间的过渡态由 Apply 统一收敛，这里只处理「非切换期间的意外停止」。
    /// </summary>
    public void OnEngineRunningChanged(string engine, bool running)
    {
        if (Volatile.Read(ref _applying) != 0 ||
            !string.Equals(_context.Engine, engine, StringComparison.OrdinalIgnoreCase)) return;

        // true 只代表 actual 成功，绝不覆盖用户已经写下的 false；
        // false 必须关闭开关，不能把后台故障伪装成正在监听。
        if (!running) _context.Enabled = false;
        _context.NotifyRecognitionStateChanged();
    }

    /// <summary>
    /// 按当前意图收敛两个引擎的实际状态。可并发调用：内部串行化，
    /// 且 await 期间意图变化会重新收敛而不是用旧结果覆盖。
    /// </summary>
    public async Task ApplyAsync()
    {
        await _gate.WaitAsync();
        Interlocked.Exchange(ref _applying, 1);
        long version = Volatile.Read(ref _version);
        string engine = _context.Engine;
        try
        {
            _context.NotifyDisplaySettingsChanged();

            // await 期间用户仍可改开关/引擎。旧启动完成后先重新读取意图，
            // 必要时在释放锁之前把它停掉，而不是用 actual 事件把关闭改回开启。
            while (true)
            {
                version = Volatile.Read(ref _version);
                engine = _context.Engine;
                ISpeechEngineControl selected = Select(engine);
                ISpeechEngineControl other = ReferenceEquals(selected, _whisper) ? _liveCaptions : _whisper;

                if (!_context.Enabled)
                {
                    await _whisper.StopAsync();
                    await _liveCaptions.StopAsync();
                }
                else
                {
                    await other.StopAsync();

                    if (version != Volatile.Read(ref _version)) continue;

                    bool started = await selected.StartAsync();

                    if (version != Volatile.Read(ref _version)) continue;

                    if (!started || !selected.IsRunning) _context.Enabled = false;
                }

                if (version == Volatile.Read(ref _version)) break;
            }
        }
        catch (Exception ex)
        {
            // 仅回滚同一请求：迟到的失败不能覆盖更新的开启/引擎选择。
            if (version == Volatile.Read(ref _version) &&
                string.Equals(engine, _context.Engine, StringComparison.OrdinalIgnoreCase))
            {
                _context.Enabled = false;
            }
            _context.SetStatus($"应用语音状态失败: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _applying, 0);
            _gate.Release();
            _context.NotifyRecognitionStateChanged();
        }
    }

    private ISpeechEngineControl Select(string engine) =>
        string.Equals(engine, _liveCaptions.Name, StringComparison.OrdinalIgnoreCase) ? _liveCaptions : _whisper;
}
