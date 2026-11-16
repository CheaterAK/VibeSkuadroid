using System.Collections;
using System.Drawing;

namespace System.Windows.Forms;

/// <summary>Mirrors <c>System.Windows.Forms.MethodInvoker</c>.</summary>
public delegate void MethodInvoker();

/// <summary>Mirrors <c>System.Windows.Forms.DockStyle</c>.</summary>
public enum DockStyle
{
    None = 0,
    Top = 1,
    Bottom = 2,
    Left = 3,
    Right = 4,
    Fill = 5,
}

/// <summary>Mirrors <c>System.Windows.Forms.FormStartPosition</c>.</summary>
public enum FormStartPosition
{
    Manual = 0,
    CenterScreen = 1,
    WindowsDefaultLocation = 2,
    WindowsDefaultBounds = 3,
    CenterParent = 4,
}

/// <summary>Mirrors <c>System.Windows.Forms.FormBorderStyle</c>.</summary>
public enum FormBorderStyle
{
    None = 0,
    FixedSingle = 1,
    Fixed3D = 2,
    FixedDialog = 3,
    Sizable = 4,
    FixedToolWindow = 5,
    SizableToolWindow = 6,
}

/// <summary>Mirrors <c>System.Windows.Forms.ProgressBarStyle</c>.</summary>
public enum ProgressBarStyle
{
    Blocks = 0,
    Continuous = 1,
    Marquee = 2,
}

/// <summary>
/// Stand-in for <c>System.Windows.Forms.PaintEventArgs</c>. It has no <c>Graphics</c>: there is nothing to draw on.
/// </summary>
public class PaintEventArgs : EventArgs
{
}

/// <summary>Mirrors <c>System.Windows.Forms.PaintEventHandler</c>.</summary>
public delegate void PaintEventHandler(object? sender, PaintEventArgs e);

/// <summary>
/// Headless stand-in for <c>System.Windows.Forms.Control</c>. It only holds state; nothing is ever shown,
/// and <see cref="Invoke(Delegate)"/> runs the delegate synchronously on the calling thread.
/// </summary>
public class Control : IDisposable
{
    public Control()
    {
        Controls = new ControlCollection(this);
    }

    public string Text { get; set; } = string.Empty;

    public Size Size { get; set; }

    public int Width
    {
        get => Size.Width;
        set => Size = new Size(value, Size.Height);
    }

    public int Height
    {
        get => Size.Height;
        set => Size = new Size(Size.Width, value);
    }

    public DockStyle Dock { get; set; }

    public Color ForeColor { get; set; }

    public Color BackColor { get; set; }

    public bool Visible { get; set; }

    public bool IsDisposed { get; private set; }

    public ControlCollection Controls { get; }

    public event PaintEventHandler? Paint;

    public object? Invoke(Delegate method) => method.DynamicInvoke();

    public object? Invoke(Delegate method, params object?[]? args) => method.DynamicInvoke(args);

    public void Show() => Visible = true;

    public void Hide() => Visible = false;

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        IsDisposed = true;
        Paint = null;
    }

    /// <summary>Mirrors <c>System.Windows.Forms.Control.ControlCollection</c>.</summary>
    public class ControlCollection : IEnumerable
    {
        private readonly List<Control> _controls = new();

        public ControlCollection(Control owner)
        {
            Owner = owner;
        }

        public Control Owner { get; }

        public int Count => _controls.Count;

        public void Add(Control? value)
        {
            if (value is not null)
                _controls.Add(value);
        }

        public void Remove(Control? value)
        {
            if (value is not null)
                _controls.Remove(value);
        }

        public void Clear() => _controls.Clear();

        public IEnumerator GetEnumerator() => _controls.GetEnumerator();
    }
}

/// <summary>Headless stand-in for <c>System.Windows.Forms.ProgressBar</c>.</summary>
public class ProgressBar : Control
{
    public int Minimum { get; set; }

    public int Maximum { get; set; } = 100;

    public int Value { get; set; }

    public int Step { get; set; } = 10;

    public ProgressBarStyle Style { get; set; }

    public void PerformStep() => Value = Math.Min(Maximum, Value + Step);
}

/// <summary>
/// Headless stand-in for <c>System.Windows.Forms.Form</c>. It is never shown, so <see cref="Shown"/> is never raised.
/// </summary>
public class Form : Control
{
    public FormStartPosition StartPosition { get; set; }

    public FormBorderStyle FormBorderStyle { get; set; } = FormBorderStyle.Sizable;

    public bool MaximizeBox { get; set; } = true;

    public bool MinimizeBox { get; set; } = true;

    public bool TopMost { get; set; }

    public event EventHandler? Shown;

    public DialogResult ShowDialog() => DialogResult.Cancel;

    public void Close() => Visible = false;

    protected override void Dispose(bool disposing)
    {
        Shown = null;
        base.Dispose(disposing);
    }
}

/// <summary>
/// Headless stand-in for <c>System.Windows.Forms.Application</c>. There is no message loop:
/// <see cref="Run(Form)"/> returns immediately without showing the form.
/// </summary>
public static class Application
{
    public static void Run()
    {
    }

    public static void Run(Form mainForm)
    {
    }

    public static void DoEvents()
    {
    }

    public static void Exit()
    {
    }
}
