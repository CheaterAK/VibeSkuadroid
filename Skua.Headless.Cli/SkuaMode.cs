using Skua.Core.Headless;
using Skua.Core.Headless.Host;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using System.Diagnostics;
using System.Globalization;

namespace Skua.Headless.Cli;

/// <summary>Runs or compiles a Skua script with unmodified Skua.Core on the headless bridge.</summary>
internal static class SkuaMode
{
    private static HeadlessSkuaOptions Options(string? dataDir, string? server)
    {
        return new HeadlessSkuaOptions
        {
            DataDirectory = dataDir,
            Log = e => Console.Error.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{e.Source}] {e.Message}"),
        };
    }

    public static async Task<int> CompileAsync(string path, string? dataDir)
    {
        await using HeadlessSkuaSession session = HeadlessSkuaSession.Create(Options(dataDir, null));
        Stopwatch sw = Stopwatch.StartNew();
        string full = Path.GetFullPath(path);
        session.Scripts.SetLoadedScript(full);
        try
        {
            object? script = await Task.Run(() => session.Scripts.Compile(File.ReadAllText(full)));
            Console.WriteLine($"Compiled {Path.GetFileName(full)} in {sw.ElapsedMilliseconds} ms: {script?.GetType().FullName ?? "(no type)"}");
            return script is null ? 4 : 0;
        }
        catch (ScriptCompileException e)
        {
            Console.WriteLine($"Compile failed for {Path.GetFileName(full)}: {e.Message}");
            return 4;
        }
    }

    public static async Task<int> RunAsync(AqwCredentials credentials, string path, Dictionary<string, string?> args)
    {
        await using HeadlessSkuaSession session = HeadlessSkuaSession.Create(Options(args.GetValueOrDefault("--data-dir"), null));
        using CancellationTokenSource cts = new();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        int exit = 0;
        try
        {
            if (!await session.LoginAsync(credentials.Username, credentials.Password, args.GetValueOrDefault("--server"), cts.Token))
            {
                Console.Error.WriteLine($"Login failed: {session.Game.ModalMessage ?? session.Game.ConnectionDetail ?? "not in game"}");
                return 3;
            }
            TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
            session.ScriptRunningChanged += (_, running) =>
            {
                if (!running)
                    stopped.TrySetResult();
            };
            session.ScriptError += (_, e) => Console.Error.WriteLine($"Script error: {e}");
            Exception? start = await session.StartScriptAsync(path);
            if (start is not null)
            {
                Console.Error.WriteLine($"Script did not start: {start.Message}");
                exit = 4;
            }
            else
            {
                int timeout = int.TryParse(args.GetValueOrDefault("--timeout"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int s) ? s : -1;
                Task waiting = timeout > 0 ? stopped.Task.WaitAsync(TimeSpan.FromSeconds(timeout), cts.Token) : stopped.Task.WaitAsync(cts.Token);
                try
                {
                    await waiting;
                }
                catch (Exception e) when (e is TimeoutException or OperationCanceledException)
                {
                    Console.Error.WriteLine("Stopping the script.");
                    await session.StopScriptAsync();
                }
            }
            HeadlessSkuaStatus st = session.GetStatus();
            Console.WriteLine($"Status: {st.State}, map {st.Map}/{st.Cell}, kills {st.Kills}, deaths {st.Deaths}, quests {st.QuestsCompleted}, drops {st.Drops}, relogins {st.Relogins}");
            await session.LogoutAsync();
        }
        catch (OperationCanceledException)
        {
            exit = 130;
        }
        Console.WriteLine(session.Gaps.Format());
        return exit;
    }
}
