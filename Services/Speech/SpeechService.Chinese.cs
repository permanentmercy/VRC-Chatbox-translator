using System;
using System.Runtime.InteropServices;

namespace VrcChatboxDemo.Services;

public partial class SpeechService
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int LCMapStringEx(
        string? lpLocaleName,
        uint dwMapFlags,
        string lpSrcStr,
        int cchSrc,
        [Out] char[]? lpDestStr,
        int cchDest,
        IntPtr lpVersionInformation,
        IntPtr lpReserved,
        IntPtr sortHandle);

    private const uint LCMAP_SIMPLIFIED_CHINESE = 0x02000000;
    private const uint LCMAP_TRADITIONAL_CHINESE = 0x04000000;

    public string ChineseVariant { get; set; } = "Simplified";

    public static string ConvertChineseVariant(string text, string? variant)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        if (string.IsNullOrEmpty(variant) || string.Equals(variant, "Original", StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        uint flag = string.Equals(variant, "Traditional", StringComparison.OrdinalIgnoreCase)
            ? LCMAP_TRADITIONAL_CHINESE
            : LCMAP_SIMPLIFIED_CHINESE;

        string locale = string.Equals(variant, "Traditional", StringComparison.OrdinalIgnoreCase)
            ? "zh-Hant-TW"
            : "zh-Hans-CN";

        int len = LCMapStringEx(locale, flag, text, text.Length, null, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (len <= 0) return text;

        char[] result = new char[len];
        int written = LCMapStringEx(locale, flag, text, text.Length, result, len, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        return written > 0 ? new string(result, 0, written) : text;
    }
}
