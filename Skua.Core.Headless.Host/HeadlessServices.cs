using Skua.Core.Interfaces;
using Skua.Core.Interfaces.ViewModels;
using Skua.Core.Models;
using Skua.Core.Services;
using System.Diagnostics;

namespace Skua.Core.Headless.Host;

/// <summary>A log line from Skua (script, debug or flash log) or from the headless layers.</summary>
/// <param name="Source">"script", "debug", "flash", "game" (headless client) or "dialog".</param>
public sealed record HeadlessLogEntry(string Source, string Message, DateTimeOffset At);

/// <summary>
/// How unattended dialogs are answered. Message boxes never block: yes/no questions get <see cref="YesNoAnswer"/>,
/// button choices the button named in <see cref="PreferredButtons"/> (first match) or the first button, and script
/// option windows are accepted as they are (their saved values are used).
/// </summary>
public sealed class HeadlessDialogPolicy
{
    /// <summary>Answer to yes/no questions. Default no: scripts ask before doing something optional.</summary>
    public bool YesNoAnswer { get; set; }

    /// <summary>Buttons to choose when offered, in order of preference (case-insensitive).</summary>
    public IList<string> PreferredButtons { get; } = new List<string>();

    /// <summary>
    /// Called with every question before the default answer is used; return a value to answer it yourself
    /// (an index into the buttons, or for yes/no 1 = yes, 0 = no), null for the default. Must not block for long.
    /// </summary>
    public Func<string, string, IReadOnlyList<string>, int?>? Answer { get; set; }
}

/// <summary>Dialogs without a screen: logged and answered by <see cref="HeadlessDialogPolicy"/>.</summary>
public sealed class HeadlessDialogService : IDialogService
{
    private readonly HeadlessDialogPolicy _policy;
    private readonly Action<HeadlessLogEntry> _log;

    public HeadlessDialogService(HeadlessDialogPolicy policy, Action<HeadlessLogEntry> log)
    {
        _policy = policy;
        _log = log;
    }

    public bool? ShowDialog<TViewModel>(TViewModel viewModel) where TViewModel : class => true;

    public bool? ShowDialog<TViewModel>(TViewModel viewModel, string Title) where TViewModel : class => true;

    public bool? ShowDialog<TViewModel>(TViewModel viewModel, Action<TViewModel> callback) where TViewModel : class
    {
        callback(viewModel);
        return true;
    }

    // Script options: kept as saved (no window to change them in).
    public void ShowDialog(IOptionContainer optionContainer, Action<IOptionContainerViewModel> callback)
    {
        _log(new HeadlessLogEntry("dialog", "Script options window skipped; saved options are used.", DateTimeOffset.UtcNow));
    }

    public void ShowMessageBox(string message, string caption)
    {
        _log(new HeadlessLogEntry("dialog", $"{caption}: {message}", DateTimeOffset.UtcNow));
    }

    public bool? ShowMessageBox(string message, string caption, bool yesAndNo)
    {
        if (!yesAndNo)
        {
            ShowMessageBox(message, caption);
            return true;
        }
        int? custom = _policy.Answer?.Invoke(message, caption, new[] { "Yes", "No" });
        bool answer = custom is int c ? c == 1 : _policy.YesNoAnswer;
        _log(new HeadlessLogEntry("dialog", $"{caption}: {message} -> {(answer ? "Yes" : "No")}", DateTimeOffset.UtcNow));
        return answer;
    }

    public DialogResult ShowMessageBox(string message, string caption, params string[] buttons)
    {
        int index = _policy.Answer?.Invoke(message, caption, buttons) ?? -1;
        if (index < 0 || index >= buttons.Length)
        {
            index = _policy.PreferredButtons.Select(p => Array.FindIndex(buttons, b => b.Equals(p, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(i => i >= 0, -1);
            if (index < 0)
                index = buttons.Length > 0 ? 0 : -1;
        }
        string chosen = index >= 0 ? buttons[index] : string.Empty;
        _log(new HeadlessLogEntry("dialog", $"{caption}: {message} -> {(index >= 0 ? chosen : "(no buttons)")}", DateTimeOffset.UtcNow));
        return index < 0 ? DialogResult.Cancelled : new DialogResult(chosen, index);
    }

    public IInputDialogViewModel CreateInputDialog(string title, string dialogHint) => new InputDialog(title, dialogHint);

    private sealed class InputDialog : IInputDialogViewModel
    {
        public InputDialog(string title, string hint)
        {
            DialogHint = title;
            TextBoxHint = hint;
        }

        public bool NumberOnly => false;

        public string DialogTextInput => string.Empty;

        public string DialogHint { get; }

        public string TextBoxHint { get; }
    }
}

/// <summary>Skua's logs kept in memory and forwarded to the host's sink; also catches Trace output (debug log).</summary>
public sealed class HeadlessLogService : ILogService
{
    private const int Keep = 2000;
    private readonly Dictionary<LogType, List<string>> _logs = new() { [LogType.Debug] = new(), [LogType.Script] = new(), [LogType.Flash] = new() };
    private readonly Action<HeadlessLogEntry> _sink;

    public HeadlessLogService(Action<HeadlessLogEntry> sink)
    {
        _sink = sink;
    }

    private void Add(LogType type, string source, string? message)
    {
        if (message is null)
            return;
        lock (_logs)
        {
            List<string> l = _logs[type];
            if (l.Count >= Keep)
                l.RemoveAt(0);
            l.Add(message);
        }
        _sink(new HeadlessLogEntry(source, message, DateTimeOffset.UtcNow));
    }

    public void DebugLog(string message) => Add(LogType.Debug, "debug", message);

    public void ScriptLog(string message) => Add(LogType.Script, "script", message);

    public void FlashLog(string message) => Add(LogType.Flash, "flash", message);

    public void ClearLog(LogType logType)
    {
        lock (_logs)
        {
            if (_logs.TryGetValue(logType, out List<string>? l))
                l.Clear();
        }
    }

    public List<string> GetLogs(LogType logType)
    {
        lock (_logs)
            return _logs.TryGetValue(logType, out List<string>? l) ? l.ToList() : new List<string>();
    }

    /// <summary>Forwards Trace.WriteLine (Skua's debug output) to the debug log.</summary>
    public sealed class TraceForward : TraceListener
    {
        private readonly HeadlessLogService _log;

        public TraceForward(HeadlessLogService log)
        {
            _log = log;
        }

        public override void Write(string? message)
        {
        }

        public override void WriteLine(string? message)
        {
            if (!string.IsNullOrWhiteSpace(message))
                _log.DebugLog(message);
        }
    }
}

/// <summary>Skua's settings stored the way Skua stores them (ClientFileSources.SkuaDIR).</summary>
public sealed class HeadlessSettingsService : ISettingsService, IDisposable
{
    private readonly UnifiedSettingsService _unified = new();

    public HeadlessSettingsService()
    {
        _unified.Initialize(AppRole.Client);
    }

    public T? Get<T>(string key) => _unified.Get<T>(key);

    public T Get<T>(string key, T defaultValue) => _unified.Get(key, defaultValue);

    public void Set<T>(string key, T value) => _unified.Set(key, value);

    public void Initialize(AppRole role) => _unified.Initialize(role);

    public SharedSettings GetShared() => _unified.GetShared();

    public ClientSettings GetClient() => _unified.GetClient();

    public ManagerSettings GetManager() => _unified.GetManager();

    public void SetApplicationVersion() => _unified.SetApplicationVersion();

    public void ReloadSettings() => _unified.ReloadSettings();

    public bool TryReloadSettings() => _unified.TryReloadSettings();

    public bool TryUpdate<T>(string key, T defaultValue, Func<T, T> change, out T result) => _unified.TryUpdate(key, defaultValue, change, out result);

    public void Dispose() => _unified.Dispose();
}

/// <summary>Runs dispatcher work inline (there is no UI thread).</summary>
public sealed class HeadlessDispatcher : IDispatcherService
{
    public void Invoke(Action action) => action();
}

/// <summary>No windows to show.</summary>
public sealed class HeadlessWindowService : IWindowService
{
    public void ShowWindow<TViewModel>(int width, int height) where TViewModel : class { }

    public void ShowWindow<TViewModel>() where TViewModel : class { }

    public void ShowWindow<TViewModel>(TViewModel viewModel) where TViewModel : class { }

    public void ShowManagedWindow(string key) { }

    public void RegisterManagedWindow<TViewModel>(string key, TViewModel viewModel) where TViewModel : class, IManagedWindow { }

    public void RegisterManagedWindow<TViewModel>(string key) where TViewModel : class, IManagedWindow { }

    public void RegisterManagedWindow(string key, Func<IManagedWindow> factory) { }
}

/// <summary>No file pickers: every dialog is cancelled.</summary>
public sealed class HeadlessFileDialogService : IFileDialogService
{
    public string? OpenFile() => null;

    public string? OpenFile(string filters) => null;

    public string? OpenFile(string initialDirectory, string filters) => null;

    public string? OpenFolder() => null;

    public string? OpenFolder(string initialDirectory) => null;

    public Task<string?> OpenFolderAsync() => Task.FromResult<string?>(null);

    public Task<string?> OpenFolderAsync(string initialDirectory) => Task.FromResult<string?>(null);

    public IEnumerable<string>? OpenText() => null;

    public string? Save() => null;

    public string? Save(string filters) => null;

    public string? Save(string initialDirectory, string filters) => null;

    public void SaveText(string contents) { }

    public void SaveText(IEnumerable<string> contents) { }
}

/// <summary>A clipboard in memory.</summary>
public sealed class HeadlessClipboard : IClipboardService
{
    private readonly Dictionary<string, object> _data = new();
    private string _text = string.Empty;

    public void SetText(string text) => _text = text;

    public void SetData(string format, object data) => _data[format] = data;

    public object GetData(string format) => _data.TryGetValue(format, out object? o) ? o : null!;

    public string GetText() => _text;
}

/// <summary>No hot keys without a keyboard.</summary>
public sealed class HeadlessHotKeys : IHotKeyService
{
    public void Reload() { }

    public List<T> GetHotKeys<T>() where T : IHotKey, new() => new();

    public HotKey? ParseToHotKey(string keyGesture) => null;
}

/// <summary>No sound.</summary>
public sealed class HeadlessSound : ISoundService
{
    public void Beep() { }

    public void Beep(int frequency, int duration) { }
}

/// <summary>Links are logged (or handed to the platform); there is no editor to open.</summary>
public sealed class HeadlessProcessService : IProcessService
{
    private readonly Action<string>? _openLink;
    private readonly Action<HeadlessLogEntry> _log;

    public HeadlessProcessService(Action<string>? openLink, Action<HeadlessLogEntry> log)
    {
        _openLink = openLink;
        _log = log;
    }

    public void OpenLink(string link)
    {
        if (_openLink is not null)
            _openLink(link);
        else
            _log(new HeadlessLogEntry("dialog", "Link not opened (headless): " + link, DateTimeOffset.UtcNow));
    }

    public void OpenVSC() { }

    public void OpenVSC(string path) { }
}

/// <summary>Nothing is drawn, so there is nothing to capture: screenshots are empty.</summary>
public sealed class HeadlessScreenshotService : IScreenshotService
{
    public Task<byte[]> TakeScreenshotAsync() => Task.FromResult(Array.Empty<byte>());
}
