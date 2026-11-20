namespace Skua.App.Droid.Core.Scheduler;

/// <summary>
/// Turns a script failure into one plain-English sentence for the queue list. The desktop scheduler unwraps the
/// TargetInvocationException around a script's exception and shows its message; the host adapter already does that
/// ("TypeName: first line" or "Compile error: ..."), and this adds what the type means for someone who does not read C#.
/// </summary>
public static class ErrorText
{
    private static readonly (string Type, string Meaning)[] s_types =
    [
        ("NullReferenceException", "it used something that was not there (often an item, monster or map that did not load)"),
        ("IndexOutOfRangeException", "it read past the end of a list"),
        ("ArgumentOutOfRangeException", "it read past the end of a list or got a value out of range"),
        ("KeyNotFoundException", "it looked up something that does not exist"),
        ("InvalidCastException", "it got data of an unexpected kind"),
        ("FormatException", "it could not read a number or text it expected"),
        ("InvalidOperationException", "it did something the current game state does not allow"),
        ("TimeoutException", "it waited too long for the game"),
        ("FileNotFoundException", "a file it needs is missing"),
        ("DirectoryNotFoundException", "a folder it needs is missing"),
        ("IOException", "it could not read or write a file"),
        ("UnauthorizedAccessException", "it was not allowed to read or write a file"),
        ("PlatformNotSupportedException", "it needs something only Windows has"),
        ("NotSupportedException", "it uses something this app does not support"),
        ("NotImplementedException", "it uses something this app does not support"),
        ("MissingMethodException", "it was built for another Skua version"),
        ("TypeLoadException", "it was built for another Skua version"),
        ("StackOverflowException", "it called itself without end"),
        ("OutOfMemoryException", "the device ran out of memory"),
    ];

    /// <summary>One sentence for <paramref name="message"/> (an engine or script error message).</summary>
    public static string Summarize(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "The script stopped with an error.";
        string text = message.Trim();
        int newline = text.IndexOf('\n');
        if (newline >= 0)
            text = text[..newline].TrimEnd('\r');
        if (text.Length > 240)
            text = text[..240] + "...";

        if (text.StartsWith("Compile error:", StringComparison.Ordinal))
            return "The script does not compile: " + text["Compile error:".Length..].Trim();
        if (text.StartsWith("No script at", StringComparison.Ordinal) || text.Contains("script file does not exist", StringComparison.OrdinalIgnoreCase))
            return "The script file is missing (deleted or renamed?).";
        if (text.Contains("already running", StringComparison.OrdinalIgnoreCase))
            return "Another script was already running.";
        if (text.Contains("Wait for the login", StringComparison.Ordinal))
            return "The login was still in progress.";
        if (text.Contains("Scripts are not available", StringComparison.Ordinal) || text.Contains("script references are incomplete", StringComparison.OrdinalIgnoreCase))
            return "Scripts cannot run in this build: " + text;
        if (text.StartsWith("OperationCanceledException", StringComparison.Ordinal) || text.StartsWith("TaskCanceledException", StringComparison.Ordinal))
            return "The script was cancelled.";

        int colon = text.IndexOf(": ", StringComparison.Ordinal);
        string type = colon > 0 ? text[..colon] : string.Empty;
        string detail = colon > 0 ? text[(colon + 2)..].Trim() : text;
        if (type.Length > 0 && !type.Contains(' ', StringComparison.Ordinal))
        {
            string shortType = type[(type.LastIndexOf('.') + 1)..];
            foreach ((string Type, string Meaning) t in s_types)
            {
                if (shortType == t.Type)
                    return $"The script crashed: {t.Meaning} ({shortType}{(detail.Length > 0 ? ": " + detail : string.Empty)}).";
            }
            if (shortType.EndsWith("Exception", StringComparison.Ordinal))
                return $"The script crashed ({shortType}){(detail.Length > 0 ? ": " + detail : ".")}";
        }
        return text.EndsWith('.') ? text : text + ".";
    }
}
