using System.Text.Json;

namespace Skua.App.Droid.Core.Hosting;

/// <summary>Written just before a planned process restart; read once by the next process. Holds no secrets.</summary>
/// <param name="ResumeLogin">Log back in after the restart (the password comes from the vault, if remembered).</param>
/// <param name="Account">The account that was online.</param>
/// <param name="Server">The server it was on.</param>
/// <param name="Reason">Why the restart happened (shown in the log).</param>
/// <param name="At">When the restart was requested.</param>
public sealed record ResumeRequest(bool ResumeLogin, string? Account, string? Server, string Reason, DateTimeOffset At);

/// <summary>One-shot file handing a <see cref="ResumeRequest"/> from a process to its replacement.</summary>
public sealed class ResumeStore
{
    /// <summary>A request older than this is ignored (the restart did not happen promptly; the user moved on).</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);

    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly object _lock = new();

    public ResumeStore(string path, TimeProvider? time = null)
    {
        _path = path;
        _time = time ?? TimeProvider.System;
    }

    public void Write(ResumeRequest request)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(request));
            File.Move(temp, _path, overwrite: true);
        }
    }

    /// <summary>Returns the pending request and deletes it, so it is acted on at most once.</summary>
    public ResumeRequest? Take()
    {
        lock (_lock)
        {
            if (!File.Exists(_path))
                return null;
            ResumeRequest? request = null;
            try
            {
                request = JsonSerializer.Deserialize<ResumeRequest>(File.ReadAllText(_path));
            }
            catch (Exception e) when (e is IOException or JsonException)
            {
            }
            try
            {
                File.Delete(_path);
            }
            catch (IOException)
            {
            }
            if (request is null || _time.GetUtcNow() - request.At > MaxAge)
                return null;
            return request;
        }
    }
}
