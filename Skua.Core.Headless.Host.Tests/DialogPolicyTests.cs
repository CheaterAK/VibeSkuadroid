using Skua.Core.Interfaces;
using Skua.Core.Interfaces.ViewModels;

namespace Skua.Core.Headless.Host.Tests;

/// <summary>Unattended dialogs: declining answers by default, nothing confirmed on the user's behalf.</summary>
public sealed class DialogPolicyTests
{
    private sealed class Confirm
    {
        public int Quantity { get; set; } = 5;
    }

    [Fact]
    public void ConfirmationAndInputDialogsAreDeclinedAndTheirCallbackDoesNotRun()
    {
        List<HeadlessLogEntry> log = new();
        IDialogService dialogs = new HeadlessDialogService(new HeadlessDialogPolicy(), log.Add);
        IInputDialogViewModel input = dialogs.CreateInputDialog("Buying Sword", "Buy quantity (Cost: 10 AC)");

        Assert.False(dialogs.ShowDialog(input));
        Assert.False(dialogs.ShowDialog(new Confirm(), "Sell?"));
        bool ran = false;
        Assert.False(dialogs.ShowDialog(new Confirm(), _ => ran = true));
        Assert.False(ran);
        Assert.Equal(3, log.Count(l => l.Source == "dialog" && l.Message.EndsWith("declined (no one to answer)", StringComparison.Ordinal)));
    }

    [Fact]
    public void ADecliningButtonWinsOverOkUnlessItIsTheOnlyChoice()
    {
        List<HeadlessLogEntry> log = new();
        HeadlessDialogPolicy policy = new();
        policy.PreferredButtons.Add("OK");
        policy.PreferredButtons.Add("Continue");
        HeadlessDialogService dialogs = new(policy, log.Add);

        Assert.Equal("Cancel", dialogs.ShowMessageBox("Download now?", "Update Available", "OK", "Cancel").Text);
        Assert.Equal(1, dialogs.ShowMessageBox("Download now?", "Update Available", "OK", "Cancel").Value);
        Assert.Equal("Cancelar", dialogs.ShowMessageBox("Baixar agora?", "Atualização disponível", "OK", "Cancelar").Text);
        Assert.Equal("No", dialogs.ShowMessageBox("Buy?", "Q", "Continue", "Yes", "No").Text);
        Assert.Equal("Abort", dialogs.ShowMessageBox("Go on?", "Q", "Continue", "Abort").Text);
        Assert.Equal("Close", dialogs.ShowMessageBox("Open?", "Q", "OK", "Close").Text);
        Assert.Equal("OK", dialogs.ShowMessageBox("Notice", "Q", "OK").Text);
        Assert.Equal("Cancel", dialogs.ShowMessageBox("Only cancel", "Q", "Cancel").Text);
        Assert.Equal("Continue", dialogs.ShowMessageBox("Retry or continue?", "Q", "Retry", "Continue").Text);
        Assert.False(dialogs.ShowMessageBox("Join?", "Q", true));

        // A host's own answer still comes first.
        policy.Answer = (_, _, buttons) => Array.IndexOf(buttons.ToArray(), "OK");
        Assert.Equal("OK", dialogs.ShowMessageBox("Download now?", "Update Available", "OK", "Cancel").Text);
    }
}
