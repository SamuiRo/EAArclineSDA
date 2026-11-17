using Arcline.SteamGuard.Steam;
using Avalonia.Threading;

namespace Arcline.SDA.Desktop.Services;

/// <summary>Answers SteamKit2's login questions with dialogs (marshalled to the UI thread).</summary>
public sealed class GuiLoginPrompts(Dialogs dialogs) : ILoginPrompts
{
    public Task<string?> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect) =>
        Dispatcher.UIThread.InvokeAsync(() => dialogs.PromptAsync("Steam Guard",
            previousCodeWasIncorrect ? $"That code was wrong. Enter the new code sent to {email}:" : $"Enter the code Steam sent to {email}:"));

    public Task<string?> GetDeviceCodeAsync(bool previousCodeWasIncorrect) =>
        Dispatcher.UIThread.InvokeAsync(async () => (await dialogs.PromptAsync("Steam Guard",
            previousCodeWasIncorrect ? "That code was wrong. Enter the current Steam Guard code:" : "Enter the current Steam Guard code from your existing authenticator:"))?.Trim().ToUpperInvariant());
}
