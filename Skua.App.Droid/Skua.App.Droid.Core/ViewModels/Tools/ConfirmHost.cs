using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Skua.App.Droid.Core.ViewModels;

/// <summary>
/// The app's own confirmation for a Tools page (the view shows it as an overlay card): a question with Confirm and
/// Cancel, optionally a quantity. Skua's dialog service is never used for these (on this client it declines every
/// unattended confirmation). One question at a time; a new question cancels an open one.
/// </summary>
public sealed partial class ConfirmHost : ObservableObject
{
    public const string ScriptRunningTitle = "A script is running";

    private TaskCompletionSource<int?>? _answer;

    /// <summary>A question is shown.</summary>
    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _text = string.Empty;

    [ObservableProperty]
    private string _button = "OK";

    /// <summary>The confirm button is shown as dangerous (spending, selling, overriding a running script).</summary>
    [ObservableProperty]
    private bool _danger;

    [ObservableProperty]
    private bool _hasQuantity;

    [ObservableProperty]
    private string _quantityLabel = "Quantity";

    [ObservableProperty]
    private decimal? _quantity = 1;

    [ObservableProperty]
    private decimal _quantityMin = 1;

    [ObservableProperty]
    private decimal _quantityMax = 1;

    /// <summary>Asks a yes/no question; true when confirmed.</summary>
    public async Task<bool> AskAsync(string title, string text, string button, bool danger = false) =>
        await Open(title, text, button, danger, null).ConfigureAwait(true) is not null;

    /// <summary>Asks for a quantity in [<paramref name="min"/>, <paramref name="max"/>]; null when cancelled.</summary>
    public Task<int?> AskQuantityAsync(string title, string text, string button, string label, int initial, int min, int max, bool danger = false) =>
        Open(title, text, button, danger, (label, initial, min, max));

    /// <summary>
    /// While a script runs: "A script is running; do it anyway?" (true when there is no script or the user agrees).
    /// </summary>
    public async Task<bool> AllowWhileScriptRunsAsync(bool scriptBusy, string what)
    {
        if (!scriptBusy)
            return true;
        return await AskAsync(ScriptRunningTitle, $"A script is running; do it anyway?\n\n{what}", "Do it anyway", danger: true).ConfigureAwait(true);
    }

    private Task<int?> Open(string title, string text, string button, bool danger, (string Label, int Initial, int Min, int Max)? quantity)
    {
        _answer?.TrySetResult(null);
        TaskCompletionSource<int?> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _answer = answer;
        Title = title;
        Text = text;
        Button = button;
        Danger = danger;
        HasQuantity = quantity is not null;
        if (quantity is { } q)
        {
            int min = Math.Max(1, q.Min);
            int max = Math.Max(min, q.Max);
            QuantityLabel = q.Label;
            QuantityMin = min;
            QuantityMax = max;
            Quantity = Math.Clamp(q.Initial, min, max);
        }
        IsOpen = true;
        return answer.Task;
    }

    [RelayCommand]
    private void Confirm()
    {
        TaskCompletionSource<int?>? answer = _answer;
        int value = HasQuantity ? (int)Math.Clamp(Quantity ?? QuantityMin, QuantityMin, QuantityMax) : 1;
        _answer = null;
        IsOpen = false;
        answer?.TrySetResult(value);
    }

    [RelayCommand]
    private void Cancel()
    {
        TaskCompletionSource<int?>? answer = _answer;
        _answer = null;
        IsOpen = false;
        answer?.TrySetResult(null);
    }

    /// <summary>System back or the page closing: cancels an open question. False when none was open.</summary>
    public bool CancelIfOpen()
    {
        if (!IsOpen)
            return false;
        Cancel();
        return true;
    }
}
