using Skua.App.Droid.Core.Hosting;
using Skua.App.Droid.Core.Platform;
using Skua.App.Droid.Core.Scripts;

namespace Skua.App.Droid.Core.ViewModels;

/// <summary>What the ViewModels are built from. One set per process.</summary>
public sealed record AppServices(
    EngineHost Host,
    IUiDispatcher Dispatcher,
    IFilePicker FilePicker,
    ScriptLibrary Library,
    IScriptRepositorySync RepositorySync,
    AppPaths Paths);
