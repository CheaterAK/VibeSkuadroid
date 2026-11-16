using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Skua.Core.Interfaces;
using Skua.Core.Interfaces.Services;
using Skua.Core.Scripts;
using Skua.Core.Services;
using Skua.Core.Utils;

namespace Skua.Core.AppStartup;

public static class Services
{
    public static IServiceCollection AddCommonServices(this IServiceCollection services)
    {
        services.AddTransient(typeof(Lazy<>), typeof(LazyInstance<>));

        services.AddSingleton(typeof(IMessenger), WeakReferenceMessenger.Default);
        services.AddSingleton(typeof(StrongReferenceMessenger), StrongReferenceMessenger.Default);

        services.AddSingleton<IDecamelizer, Decamelizer>();
        services.AddSingleton<IGetScriptsService, GetScriptsService>();
        services.AddSingleton<IProcessService, ProcessStartService>();
        services.AddSingleton<IDiagnosticsService, DiagnosticsService>();
        services.AddSingleton<ICustomScriptService, CustomScriptService>();
        services.AddSingleton<IScriptPathResolver, ScriptPathResolver>();
        services.AddSingleton<ILoadoutService, LoadoutService>();
        services.AddSingleton<IDiscordWebhookService, DiscordWebhookService>();
        services.AddSingleton<IScriptWatchdogService, ScriptWatchdogService>();

        return services;
    }

    /// <summary>
    /// Creates a script compiler with the host's base references and default usings.
    /// </summary>
    /// <remarks>
    /// Uses the <see cref="IScriptReferenceProvider"/> and <see cref="IScriptNamespaceProvider"/> registered in
    /// <paramref name="s"/>, falling back to <see cref="DesktopScriptReferenceProvider"/> and
    /// <see cref="ScriptNamespaceProvider.Desktop"/> when none is registered.
    /// </remarks>
    public static Compiler CreateCompiler(IServiceProvider s)
    {
        Compiler compiler = new();

        IScriptReferenceProvider referenceProvider = s.GetService<IScriptReferenceProvider>() ?? DesktopScriptReferenceProvider.Shared;
        IScriptNamespaceProvider namespaceProvider = s.GetService<IScriptNamespaceProvider>() ?? ScriptNamespaceProvider.Desktop;

        compiler.AddAssemblies(referenceProvider.GetReferences());
        compiler.AddNamespaces(namespaceProvider.GetNamespaces().ToArray());
        compiler.SaveGeneratedCode = true;
        return compiler;
    }
}
