using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Skua.Core.Interfaces;
using Skua.Core.Models.Packets;
using Skua.App.Avalonia.ViewModels.Packets;

namespace Skua.App.Avalonia.AppStartup;

internal static class PacketInterceptor
{
    internal static PacketInterceptorViewModel CreateViewModel(IServiceProvider s)
    {
        return new PacketInterceptorViewModel(CreateFilters(), s.GetRequiredService<ICaptureProxy>(), s.GetRequiredService<IScriptServers>(), s.GetRequiredService<IFileDialogService>());
    }

    // Each filter is given a packet's commands (PacketCommands.Parse), both directions of a request are listed
    // (the categories are shared with the Android app: Skua.Core.Models.Packets.PacketCategories)
    internal static List<PacketLogFilterViewModel> CreateFilters()
    {
        List<PacketLogFilterViewModel> filters = new();
        foreach ((string name, string[] commands) in PacketCategories.All)
            filters.Add(Category(name, commands));

        List<PacketLogFilterViewModel> specificFilters = new(filters);
        filters.Add(new(PacketCategories.Other, p =>
        {
            foreach (PacketLogFilterViewModel f in specificFilters)
            {
                if (f.Filter.Invoke(p))
                    return false;
            }
            return true;
        }));

        return filters;
    }

    private static PacketLogFilterViewModel Category(string name, params string[] commands)
    {
        HashSet<string> known = new(commands);
        return new(name, p => p.Any(known.Contains));
    }
}
