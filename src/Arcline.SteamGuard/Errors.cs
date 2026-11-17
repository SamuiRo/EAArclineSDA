namespace Arcline.SteamGuard;

public class SteamGuardException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The refresh token is missing or expired; the user must log in again.</summary>
public sealed class SessionExpiredException(string accountName)
    : SteamGuardException($"Session for '{accountName}' has expired. Log in again.");

/// <summary>Wrong passphrase, or the file was modified (authentication tag mismatch).</summary>
public sealed class WrongPassphraseException()
    : SteamGuardException("Wrong passphrase or the encrypted file is corrupted.");

public sealed class SteamApiException(string message, int? httpStatus = null, Exception? inner = null)
    : SteamGuardException(message, inner)
{
    public int? HttpStatus { get; } = httpStatus;
}
