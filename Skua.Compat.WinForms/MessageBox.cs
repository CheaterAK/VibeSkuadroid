using System.Diagnostics;

namespace System.Windows.Forms;

/// <summary>Mirrors <c>System.Windows.Forms.DialogResult</c>.</summary>
public enum DialogResult
{
    None = 0,
    OK = 1,
    Cancel = 2,
    Abort = 3,
    Retry = 4,
    Ignore = 5,
    Yes = 6,
    No = 7,
    TryAgain = 10,
    Continue = 11,
}

/// <summary>Mirrors <c>System.Windows.Forms.MessageBoxButtons</c>.</summary>
public enum MessageBoxButtons
{
    OK = 0,
    OKCancel = 1,
    AbortRetryIgnore = 2,
    YesNoCancel = 3,
    YesNo = 4,
    RetryCancel = 5,
    CancelTryContinue = 6,
}

/// <summary>Mirrors <c>System.Windows.Forms.MessageBoxIcon</c>.</summary>
public enum MessageBoxIcon
{
    None = 0,
    Hand = 16,
    Stop = 16,
    Error = 16,
    Question = 32,
    Exclamation = 48,
    Warning = 48,
    Asterisk = 64,
    Information = 64,
}

/// <summary>
/// Headless stand-in for <c>System.Windows.Forms.MessageBox</c>: writes the message to the trace log and,
/// with nobody to answer, returns the declining choice for the given buttons (No, Cancel, Abort or OK).
/// </summary>
public static class MessageBox
{
    public static DialogResult Show(string? text) => Show(text, string.Empty, MessageBoxButtons.OK, MessageBoxIcon.None);

    public static DialogResult Show(string? text, string? caption) => Show(text, caption, MessageBoxButtons.OK, MessageBoxIcon.None);

    public static DialogResult Show(string? text, string? caption, MessageBoxButtons buttons) => Show(text, caption, buttons, MessageBoxIcon.None);

    public static DialogResult Show(string? text, string? caption, MessageBoxButtons buttons, MessageBoxIcon icon)
    {
        Trace.WriteLine($"[MessageBox] {caption}: {text}");
        return buttons switch
        {
            MessageBoxButtons.OKCancel => DialogResult.Cancel,
            MessageBoxButtons.AbortRetryIgnore => DialogResult.Abort,
            MessageBoxButtons.YesNoCancel => DialogResult.Cancel,
            MessageBoxButtons.YesNo => DialogResult.No,
            MessageBoxButtons.RetryCancel => DialogResult.Cancel,
            MessageBoxButtons.CancelTryContinue => DialogResult.Cancel,
            _ => DialogResult.OK,
        };
    }
}
