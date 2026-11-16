using System.Reflection;

namespace Skua.Core.PortabilityTests;

/// <summary>
/// Minimal pass/fail runner: each check prints one line, and <see cref="Report"/> returns the exit code.
/// </summary>
internal sealed class Checks
{
    private int _passed;
    private int _failed;

    public void Run(string name, Func<string> check)
    {
        try
        {
            string detail = check();
            _passed++;
            Console.WriteLine($"PASS  {name}  ({detail})");
        }
        catch (Exception ex)
        {
            _failed++;
            Exception root = ex is TargetInvocationException { InnerException: not null } tie ? tie.InnerException : ex;
            Console.WriteLine($"FAIL  {name}");
            Console.WriteLine($"      {root.GetType().Name}: {root.Message}");
        }
    }

    public int Report()
    {
        Console.WriteLine();
        Console.WriteLine($"{_passed} passed, {_failed} failed on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
        return _failed == 0 ? 0 : 1;
    }

    public static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}

/// <summary>
/// Stand-in for the real ScriptInterface: records <c>Log</c> calls and returns defaults for everything else.
/// </summary>
public class ScriptInterfaceStub : DispatchProxy
{
    public List<string> Logs { get; } = new();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod is null)
            return null;

        if (targetMethod.Name == "Log" && args is [string message])
        {
            Logs.Add(message);
            return null;
        }

        Type returnType = targetMethod.ReturnType;
        return returnType != typeof(void) && returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
    }
}

internal static class Scripts
{
    // Relies on the default usings (System.Linq, Newtonsoft.Json.Linq, Skua.Core.Interfaces, ...).
    public const string Basic = """
        public class PortabilityScript
        {
            public void ScriptMain(IScriptInterface bot)
            {
                List<int> values = new() { 1, 2, 3 };
                bot.Log("sum=" + values.Sum());
                JObject json = JObject.Parse("{\"ok\":true}");
                bot.Log("json=" + (bool)json["ok"]!);
                bot.Log("os=" + System.Runtime.InteropServices.RuntimeInformation.OSDescription);
            }
        }
        """;

    // The WinForms surface community scripts touch: CoreBots' progress-form easter egg and the
    // MessageBox prompts in the developer tools, written the way those scripts write them.
    public const string WinForms = """
        using System.Windows.Forms;

        public class PortabilityWinFormsScript
        {
            public void ScriptMain(IScriptInterface bot)
            {
                // Qualified as the scripts do it: DialogResult is ambiguous with Skua.Core.Models.DialogResult,
                // with the real WinForms as well as with the shim.
                System.Windows.Forms.DialogResult answer = System.Windows.Forms.MessageBox.Show(
                    "Install?", "Prompt", System.Windows.Forms.MessageBoxButtons.YesNo, System.Windows.Forms.MessageBoxIcon.Question);
                bot.Log("answer=" + answer);

                bool invoked = false;
                using Form form = new()
                {
                    Text = "Progress",
                    Size = new Size(400, 100),
                    StartPosition = FormStartPosition.CenterScreen,
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    MaximizeBox = false,
                    MinimizeBox = false,
                };
                ProgressBar bar = new()
                {
                    Dock = DockStyle.Top,
                    Minimum = 0,
                    Maximum = 100,
                    Style = ProgressBarStyle.Continuous,
                    ForeColor = ColorTranslator.FromHtml("#000100"),
                    BackColor = ColorTranslator.FromHtml("#ff073a"),
                };
                form.Controls.Add(bar);
                bar.Paint += (sender, e) => { };
                form.Shown += (s, e) => bar.Value = 100;
                form.Invoke((System.Windows.Forms.MethodInvoker)(() => invoked = true));
                Application.Run(form);
                form.Close();
                bot.Log("invoked=" + invoked);
            }
        }
        """;
}
