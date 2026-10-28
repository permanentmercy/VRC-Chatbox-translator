// AppConfig 只在 XML 注释里 cref 了 SettingsService.RequestFrequencyOptions，
// 提供一个最小空壳让被链接的生产文件独立编译。
namespace VrcChatboxDemo.Services;

public sealed class SettingsService
{
    public static readonly IReadOnlyList<(string Mode, string Label, int IntervalMs, int MinNewChars)> RequestFrequencyOptions =
        new[] { (AppConfig.DefaultRequestFrequencyMode, "default", 800, 3) };
}
