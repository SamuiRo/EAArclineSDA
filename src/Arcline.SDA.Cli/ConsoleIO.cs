using System.Text;
using Arcline.SteamGuard.Steam;

namespace Arcline.SDA.Cli;

internal static class ConsoleIO
{
    public static string ReadSecret(string prompt)
    {
        if (Console.IsInputRedirected)
        {
            Console.Error.Write(prompt);
            return Console.ReadLine() ?? "";
        }
        Console.Error.Write(prompt);
        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
            if (!char.IsControl(key.KeyChar)) sb.Append(key.KeyChar);
        }
        Console.Error.WriteLine();
        return sb.ToString();
    }

    public static string? ReadLine(string prompt)
    {
        Console.Error.Write(prompt);
        return Console.ReadLine();
    }

    public static bool Confirm(string prompt, bool assumeYes)
    {
        if (assumeYes) return true;
        var answer = ReadLine(prompt + " [y/N] ");
        return answer is not null && answer.Trim().Equals("y", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Passphrase source, in order: --{name}-file, environment variable, interactive prompt.
    /// Never accepted as a plain argument (it would be visible in the process list and shell history).
    /// </summary>
    public static string GetSecret(Args args, string optionFile, string envVar, string prompt)
    {
        var file = args.Opt(optionFile);
        if (!string.IsNullOrEmpty(file)) return File.ReadAllText(file).TrimEnd('\r', '\n');
        var env = Environment.GetEnvironmentVariable(envVar);
        if (!string.IsNullOrEmpty(env)) return env;
        return ReadSecret(prompt);
    }

    public static string NewSecret(Args args, string optionFile, string envVar, string what)
    {
        var file = args.Opt(optionFile);
        if (!string.IsNullOrEmpty(file)) return File.ReadAllText(file).TrimEnd('\r', '\n');
        var env = Environment.GetEnvironmentVariable(envVar);
        if (!string.IsNullOrEmpty(env)) return env;
        while (true)
        {
            var a = ReadSecret($"New {what}: ");
            var b = ReadSecret($"Repeat {what}: ");
            if (a == b) return a;
            Console.Error.WriteLine("They do not match, try again.");
        }
    }
}

internal sealed class ConsoleLoginPrompts : ILoginPrompts
{
    public Task<string?> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect) =>
        Task.FromResult(ConsoleIO.ReadLine(previousCodeWasIncorrect
            ? $"That code was wrong. Enter the code sent to {email}: "
            : $"Enter the code Steam sent to {email}: ")?.Trim());

    public Task<string?> GetDeviceCodeAsync(bool previousCodeWasIncorrect) =>
        Task.FromResult(ConsoleIO.ReadLine(previousCodeWasIncorrect
            ? "That code was wrong. Enter the current Steam Guard code: "
            : "Enter the current Steam Guard code from your authenticator: ")?.Trim().ToUpperInvariant());
}
