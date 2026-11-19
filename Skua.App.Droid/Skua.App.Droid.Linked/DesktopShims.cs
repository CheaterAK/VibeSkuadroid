using System.Collections.Generic;
using System.Text;

namespace Skua.App.Avalonia.ViewModels.CoreBotsOptions;

/// <summary>
/// As declared in the desktop's CoreBotsViewModel.cs (not compiled here: it needs Avalonia): the CoreBots option
/// pages write their lines to the options file and read them back.
/// </summary>
internal interface IManageCBOptions
{
    StringBuilder Save(StringBuilder builder);

    void SetValues(Dictionary<string, string> values);
}
