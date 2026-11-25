using System.Text.Json;
using System.Text.Json.Serialization;

namespace Skua.App.Droid.Scripting;

/// <summary>How a compile request ended.</summary>
public enum CompileOutcome
{
    /// <summary>The script and its includes are in the compiled-script cache.</summary>
    Compiled,

    /// <summary>The script or an include does not compile: <see cref="CompileResponse.Message"/> has Skua's diagnostics.</summary>
    CompileError,

    /// <summary>The compiler failed for another reason (I/O, out of memory caught, version check): <see cref="CompileResponse.Message"/>.</summary>
    Failed,

    /// <summary>The request was refused (path outside the scripts folder, another app version's references, bad request).</summary>
    Refused,
}

/// <summary>
/// The engine asks the compiler process to compile one script (and its includes) into the compiled-script cache.
/// Carries no source and no credential: the compiler reads the script from the requesting process's own scripts folder,
/// which it shares (same HOME), and checks the path is inside it.
/// </summary>
/// <param name="Id">Correlates the response.</param>
/// <param name="ScriptPath">Full path of the script, inside the scripts folder.</param>
/// <param name="ReferenceSet">The engine's reference-set identity (<see cref="ScriptReferenceSet.Identity"/>): the compiler
/// refuses to compile against other references (an app update between the two processes).</param>
public sealed record CompileRequest(string Id, string ScriptPath, string ReferenceSet)
{
    public int V { get; init; } = ScriptCompileProtocol.Version;
}

/// <summary>The compiler's answer.</summary>
/// <param name="Id">The request's id.</param>
/// <param name="Outcome">How it ended.</param>
/// <param name="Message">Skua's compile error text, or why it failed or was refused.</param>
/// <param name="Code">For a compile error: the generated code with line numbers (Skua's ScriptCompileException.CompiledScript).</param>
/// <param name="Seconds">Time spent compiling.</param>
/// <param name="PeakRssBytes">The compiler process's peak resident memory while compiling (-1 unknown).</param>
/// <param name="Compilations">Roslyn compilations emitted (0: everything was already in the cache).</param>
public sealed record CompileResponse(string Id, CompileOutcome Outcome, string? Message, string? Code, double Seconds, long PeakRssBytes, long Compilations)
{
    public int V { get; init; } = ScriptCompileProtocol.Version;
}

/// <summary>The compiler process could not be used: not started, died (killed, out of memory), or did not answer in time.</summary>
public sealed class CompileServiceException : Exception
{
    public CompileServiceException(string message, bool died = false)
        : base(message)
    {
        Died = died;
    }

    /// <summary>The compiler process ended while compiling (killed by Android, usually for memory).</summary>
    public bool Died { get; }
}

/// <summary>Compiles scripts somewhere else (the <c>:compiler</c> process on Android; a fake in tests).</summary>
public interface IScriptCompileService
{
    /// <exception cref="CompileServiceException">The service could not be reached, died or timed out.</exception>
    Task<CompileResponse> CompileAsync(CompileRequest request, CancellationToken cancellationToken);
}

/// <summary>UTF-8 JSON encoding of <see cref="CompileRequest"/> and <see cref="CompileResponse"/> (one per Binder message).</summary>
public static class ScriptCompileProtocol
{
    public const int Version = 1;

    /// <summary>Largest message accepted (a compile error with its code listing stays far below; Binder allows ~1 MB).</summary>
    public const int MaxMessageBytes = 512 * 1024;

    private static readonly JsonSerializerOptions s_options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static byte[] Encode(CompileRequest request) => JsonSerializer.SerializeToUtf8Bytes(request, s_options);

    /// <summary>Encodes a response; a message or code listing that would not fit is cut (the head is the useful part).</summary>
    public static byte[] Encode(CompileResponse response)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(response, s_options);
        if (bytes.Length <= MaxMessageBytes)
            return bytes;
        CompileResponse cut = response with
        {
            Message = Cut(response.Message, 64 * 1024),
            Code = Cut(response.Code, 256 * 1024),
        };
        return JsonSerializer.SerializeToUtf8Bytes(cut, s_options);
    }

    public static CompileRequest? DecodeRequest(byte[]? bytes) => Decode<CompileRequest>(bytes) is { V: Version, Id.Length: > 0, ScriptPath.Length: > 0 } r ? r : null;

    public static CompileResponse? DecodeResponse(byte[]? bytes) => Decode<CompileResponse>(bytes) is { V: Version, Id.Length: > 0 } r ? r : null;

    private static T? Decode<T>(byte[]? bytes)
        where T : class
    {
        if (bytes is null || bytes.Length == 0 || bytes.Length > MaxMessageBytes)
            return null;
        try
        {
            return JsonSerializer.Deserialize<T>(bytes, s_options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Cut(string? text, int max) => text is null || text.Length <= max ? text : text[..max] + "\n... (cut)";
}
