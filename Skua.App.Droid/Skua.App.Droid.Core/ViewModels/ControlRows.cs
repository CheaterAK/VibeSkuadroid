using CommunityToolkit.Mvvm.Input;
using Skua.App.Droid.Core.Control;
using Skua.App.Droid.Core.Engine;

namespace Skua.App.Droid.Core.ViewModels;

// Rows of the Control page: a model record plus the page command the row's button runs. Immutable; a changed model
// gives a new row.

/// <summary>Which action panel of the Control page is open.</summary>
public enum ControlTab
{
    Travel,
    Shop,
    Quest,
    Bank,
}

/// <summary>A map tile.</summary>
public sealed record CellTile(string Name, string Counts, bool IsCurrent, bool HasMonsters, IRelayCommand<string?> JumpCommand);

/// <summary>A name with an action (pads, recent maps, players to go to).</summary>
public sealed record NameChip(string Name, bool IsCurrent, IRelayCommand<string?> UseCommand);

/// <summary>A monster of the current cell.</summary>
public sealed record MonsterLine(ControlMonster Model, IRelayCommand<int> AttackCommand)
{
    public int MonMapId => Model.MonMapId;

    public string Name => Model.Name;

    public string Detail => $"Lv {Model.Level} · {Model.StateText}{(Model.Targeted ? " · targeted" : string.Empty)}";

    public double HpPercent => Formatting.Percent(Model.Hp, Model.HpMax);

    public string HpText => $"{Model.Hp:N0}/{Model.HpMax:N0}";

    public bool Alive => Model.Alive;

    public bool Targeted => Model.Targeted;
}

/// <summary>Another player.</summary>
public sealed record PlayerLine(ControlPlayer Model, IRelayCommand<string?> GotoCommand)
{
    public string Name => Model.Name;

    public string Detail => $"{(Model.Level > 0 ? $"Lv {Model.Level} · " : string.Empty)}{Model.StateText} · {Model.Cell}";
}

/// <summary>A drop waiting.</summary>
public sealed record DropLine(ControlDrop Model, IRelayCommand<int> PickupCommand, IRelayCommand<int> RejectCommand)
{
    public int ItemId => Model.ItemId;

    public string Name => Model.Name;

    public string QuantityText => $"x{Model.Quantity}";
}

/// <summary>A skill bar button.</summary>
public sealed record SkillButton(ControlSkill Model, IRelayCommand<int> UseCommand)
{
    public int Index => Model.Index;

    public string Label => $"{Model.Index} {Model.Name}";
}

/// <summary>An item of the loaded shop.</summary>
public sealed record ShopLine(ControlShopItem Item, IRelayCommand<ControlShopItem?> BuyCommand)
{
    public string Name => Item.Name + (Item.Member ? " (member)" : string.Empty);

    public string Price => Item.PriceText;

    public string Requirement => Item.RequirementText;

    public bool HasRequirement => Item.RequirementText.Length > 0;

    public bool CanBuy => Item.CanBuy;
}

/// <summary>A bank or backpack item with its move (and, in the backpack while a shop is loaded, sell) action.</summary>
public sealed record ItemLine(InventoryRow Row, string MoveText, IRelayCommand<int> MoveCommand, bool CanSell, IRelayCommand<int> SellCommand)
{
    public int ItemId => Row.ItemId;

    public string Name => Row.Name;

    public string Detail => $"{Row.Type} · {Row.QuantityText}{(Row.Equipped ? " · equipped" : string.Empty)}";

    public bool CanMove => !Row.Equipped;
}
