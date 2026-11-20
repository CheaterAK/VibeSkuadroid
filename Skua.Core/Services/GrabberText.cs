using System.Text;
using Skua.Core.Models.Items;
using Skua.Core.Models.Monsters;
using Skua.Core.Models.Quests;
using Skua.Core.Models.Shops;

namespace Skua.Core.Services;

/// <summary>
/// The Grabber's text: one summary line per grabbed object (list rows, search and Copy) and the detailed export. Used
/// by the desktop Grabber window and the Android app's Grabber page.
/// </summary>
public static class GrabberText
{
    private const int MaxExportDepth = 4;

    /// <summary>"[ID] name ..." for any grabbed object.</summary>
    public static string FormatGrabbedItem(object item)
    {
        return item switch
        {
            Quest q => $"[{q.ID}] {q.Name}" + (q.Slot > 0 ? $" (Slot: {q.Slot})" : "") + (q.Upgrade ? " [Member]" : ""),
            InventoryItem inv => $"[{inv.ID}] {inv.Name} x{inv.Quantity} | Category: {inv.Category}" + (inv.Coins ? " [AC]" : "") + (inv.Upgrade ? " [Member]" : ""),
            ShopItem s => $"[{s.ID}] {s.Name} | Cost: {s.Cost:N0} {(s.Coins ? "AC" : "Gold")}" + (s.Upgrade ? " [Member]" : "") + (s.Requirements?.Count > 0 ? $" Requirements: {string.Join(", ", s.Requirements.Select(r => $"[{r.ID}] {r.Name} x{r.Quantity}"))}" : ""),
            Monster m => $"[{m.ID}] {m.Name} | Cell: {m.Cell} | HP: {m.HP}/{m.MaxHP}",
            MapItem mi => $"[{mi.ID}] Map Item (Quest: {mi.QuestID})",
            ShopInfo info => $"[{info.ID}] {info.Name}",
            ItemBase i => $"[{i.ID}] {i.Name} x{i.Quantity}" + (i.Coins ? " [AC]" : "") + (i.Upgrade ? " [Member]" : ""),
            _ => item.ToString() ?? string.Empty
        };
    }

    /// <summary>Copy: a title line and one summary line per object.</summary>
    public static string BuildExportText(string title, IEnumerable<object> items)
    {
        List<object> list = items.ToList();
        if (list.Count == 0)
            return string.Empty;

        StringBuilder sb = new();
        sb.AppendLine($"=== {title} ({list.Count} items) ===");
        foreach (object item in list)
            sb.AppendLine(FormatGrabbedItem(item));
        return sb.ToString();
    }

    /// <summary>
    /// Export: everything the grab returned, a block per item with every public property, and nested lists (quest
    /// requirements, rewards, ...) expanded underneath.
    /// </summary>
    public static string BuildDetailedExportText(string title, IEnumerable<object> items)
    {
        List<object> list = items.ToList();
        if (list.Count == 0)
            return string.Empty;

        StringBuilder sb = new();
        sb.AppendLine($"=== {title} ({list.Count} items) ===");
        foreach (object item in list)
        {
            sb.AppendLine();
            sb.AppendLine(FormatGrabbedItem(item));
            AppendProperties(sb, item, 1, new HashSet<object>(ReferenceEqualityComparer.Instance) { item });
        }
        return sb.ToString();
    }

    private static void AppendProperties(StringBuilder sb, object obj, int depth, HashSet<object> visited)
    {
        string indent = new(' ', depth * 2);
        foreach (System.Reflection.PropertyInfo property in obj.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (!property.CanRead || property.GetIndexParameters().Length > 0)
                continue;

            object? value;
            try
            {
                value = property.GetValue(obj);
            }
            catch
            {
                continue;
            }

            if (value is null)
            {
                sb.AppendLine($"{indent}{property.Name}: (none)");
            }
            else if (IsSimpleValue(value))
            {
                sb.AppendLine($"{indent}{property.Name}: {value}");
            }
            else if (value is System.Collections.IEnumerable enumerable)
            {
                List<object> entries;
                try
                {
                    entries = enumerable.Cast<object?>().Where(e => e is not null).Cast<object>().ToList();
                }
                catch
                {
                    // A collection that throws while iterated is skipped, not allowed to abort the whole export.
                    sb.AppendLine($"{indent}{property.Name}: (unreadable)");
                    continue;
                }

                sb.AppendLine($"{indent}{property.Name} ({entries.Count}):");
                foreach (object entry in entries)
                {
                    if (IsSimpleValue(entry))
                    {
                        sb.AppendLine($"{indent}  - {entry}");
                        continue;
                    }

                    sb.AppendLine($"{indent}  - {FormatGrabbedItem(entry)}");
                    if (depth < MaxExportDepth && visited.Add(entry))
                        AppendProperties(sb, entry, depth + 2, visited);
                }
            }
            else if (depth < MaxExportDepth && visited.Add(value))
            {
                sb.AppendLine($"{indent}{property.Name}:");
                AppendProperties(sb, value, depth + 1, visited);
            }
            else
            {
                sb.AppendLine($"{indent}{property.Name}: {value}");
            }
        }
    }

    private static bool IsSimpleValue(object value)
    {
        Type type = value.GetType();
        return type.IsPrimitive || type.IsEnum || value is string or decimal or DateTime or DateTimeOffset or TimeSpan or Guid;
    }
}
