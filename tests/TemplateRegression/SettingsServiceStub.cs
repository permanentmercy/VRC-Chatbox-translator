namespace VrcChatboxDemo.Services;

// The template service only needs this UI-side action during SubmitAsync.
// Parsing, variable rendering, and actual loopback OSC sending use linked production code.
public sealed class SettingsService
{
    public void SuppressTypingAnimation() { }
}
