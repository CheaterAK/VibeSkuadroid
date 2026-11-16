using System.Diagnostics;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Spike.Core;

public sealed record CompileResult(byte[]? Image, IReadOnlyList<Diagnostic> Errors, double ParseMs, double BindMs, double EmitMs, int Lines, int Chars)
{
    public bool Success => Image != null;
    public double TotalMs => ParseMs + BindMs + EmitMs;
    public string Timings => $"parse={ParseMs:F0}ms bind/diagnostics={BindMs:F0}ms emit={EmitMs:F0}ms total={TotalMs:F0}ms ({Lines} lines, {Chars} chars, image {(Image?.Length ?? 0) / 1024.0:F1}KB)";
}

/// <summary>Mirrors the options used by Skua.Core.Compiler on desktop.</summary>
public static class ScriptCompiler
{
    public static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest, DocumentationMode.None);

    public static readonly CSharpCompilationOptions Options = new(
        OutputKind.DynamicallyLinkedLibrary,
        optimizationLevel: OptimizationLevel.Release,
        concurrentBuild: true,
        deterministic: true,
        reportSuppressedDiagnostics: false);

    public static CompileResult Compile(string assemblyName, IEnumerable<(string Path, string Source)> sources, IEnumerable<MetadataReference> references)
    {
        var sw = Stopwatch.StartNew();
        var src = sources.ToList();
        var trees = src.Select(s => CSharpSyntaxTree.ParseText(s.Source, ParseOptions, s.Path)).ToList();
        // Force full parse now so it is timed separately.
        foreach (var t in trees) t.GetRoot();
        double parse = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        var compilation = CSharpCompilation.Create(assemblyName, trees, references, Options);
        var diags = compilation.GetDiagnostics();
        double bind = sw.Elapsed.TotalMilliseconds;

        var errors = diags.Where(d => d.Severity == DiagnosticSeverity.Error || d.IsWarningAsError).ToList();
        int lines = src.Sum(s => s.Source.Count(c => c == '\n') + 1);
        int chars = src.Sum(s => s.Source.Length);
        if (errors.Count > 0)
            return new CompileResult(null, errors, parse, bind, 0, lines, chars);

        sw.Restart();
        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        double emitMs = sw.Elapsed.TotalMilliseconds;
        if (!emit.Success)
            return new CompileResult(null, emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList(), parse, bind, emitMs, lines, chars);
        return new CompileResult(ms.ToArray(), [], parse, bind, emitMs, lines, chars);
    }

    public static string FormatErrors(CompileResult r, int max = 15) =>
        string.Join(Environment.NewLine, r.Errors.Take(max).Select(d => "      " + d)) +
        (r.Errors.Count > max ? $"{Environment.NewLine}      ... and {r.Errors.Count - max} more" : "");

    /// <summary>Finds the first type declaring a method named ScriptMain and invokes it, like the desktop loader.</summary>
    public static object? InvokeScriptMain(Assembly asm, params object?[] args)
    {
        foreach (var type in asm.GetTypes())
        {
            var mi = type.GetMethod("ScriptMain", BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
            if (mi == null) continue;
            object? instance = mi.IsStatic ? null : Activator.CreateInstance(type);
            return mi.Invoke(instance, args);
        }
        throw new MissingMethodException("No type with a ScriptMain method in " + asm.FullName);
    }
}
