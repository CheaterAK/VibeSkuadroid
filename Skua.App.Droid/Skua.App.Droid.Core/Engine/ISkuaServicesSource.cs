namespace Skua.App.Droid.Core.Engine;

/// <summary>Implemented by a script host that runs Skua.Core (the host adapter): Skua's services on its session.</summary>
public interface ISkuaServicesSource
{
    /// <summary>Null until the host's session exists, or when it could not start.</summary>
    IServiceProvider? SkuaServices { get; }
}
