using Arcline.SteamGuard.Steam;
using Avalonia.Threading;

namespace Arcline.SDA.Desktop.Services;

/// <summary>Answers SteamKit2's login questions with dialogs (marshalled to the UI thread).</summary>
/// <param name="linkingNewAuthenticator">
/// True in the "Add authenticator" flow. A request for a device code then means the account already has a mobile
/// authenticator, and the user is told so up front together with what happens next (moving it here via SMS).
/// </param>
public sealed class GuiLoginPrompts(Dialogs dialogs, bool linkingNewAuthenticator = false) : ILoginPrompts
{
    /// <summary>Set when Steam asked for a code from an existing authenticator during this login.</summary>
    public bool ExistingAuthenticatorDetected { get; private set; }

    public Task<string?> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect) =>
        Dispatcher.UIThread.InvokeAsync(() => dialogs.PromptAsync("Steam Guard",
            previousCodeWasIncorrect ? $"That code was wrong or expired. Enter the newest code sent to {email}:" : $"Enter the code Steam sent to {email}:"));

    public Task<string?> GetDeviceCodeAsync(bool previousCodeWasIncorrect) =>
        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (linkingNewAuthenticator && !ExistingAuthenticatorDetected)
            {
                ExistingAuthenticatorDetected = true;
                var go = await dialogs.ConfirmAsync("Account already has an authenticator",
                    "This account is already protected by a Steam Guard mobile authenticator (for example the Steam app on your phone).\n\n" +
                    "Arcline can take it over:\n" +
                    "  1. log in with a code from that authenticator,\n" +
                    "  2. confirm with an SMS code sent to the phone number on the account.\n\n" +
                    "After that the old authenticator stops working and Arcline becomes the authenticator.\n\n" +
                    "If you have this account's maFile, cancel and use \"Import maFile\" instead: nothing changes on Steam then.",
                    "Continue", "Cancel");
                if (!go) return null;
            }
            var code = await dialogs.PromptAsync("Steam Guard", previousCodeWasIncorrect
                ? "That code was wrong. Enter the CURRENT code from your existing authenticator:"
                : "Enter the current Steam Guard code from your existing authenticator (e.g. the Steam app):");
            return code?.Trim().ToUpperInvariant();
        });
}
