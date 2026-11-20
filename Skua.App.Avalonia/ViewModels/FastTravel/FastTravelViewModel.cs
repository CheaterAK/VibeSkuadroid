using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Skua.App.Avalonia.ViewModels.Dialogs;
using Skua.Core.Interfaces;
using Skua.Core.Interfaces.ViewModels;
using Skua.Core.Messaging;
using Skua.Core.Models;
using Skua.Shared.Avalonia.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;

namespace Skua.App.Avalonia.ViewModels.FastTravel;

public partial class FastTravelViewModel : BotControlViewModelBase
{
    public FastTravelViewModel(IMapService mapService, ISettingsService settings, IDialogService dialogService)
        : base("Fast Travel")
    {
        MapService = mapService;
        _settings = settings;
        _dialogService = dialogService;
        _travelCommand = new RelayCommand<object>(mapService.Travel);
        Editor = new(mapService, _travelCommand);
        List<FastTravelItemViewModel> defaultFastTravels = new();
        StringCollection? savedTravels = _settings.Get<StringCollection>("FastTravels");
        if (savedTravels != null)
        {
            foreach (string? item in savedTravels)
            {
                if (FastTravelEntry.TryParse(item, out FastTravelEntry? entry))
                    defaultFastTravels.Add(new FastTravelItemViewModel(entry!.Name, entry.Map, entry.Cell, entry.Pad, _travelCommand));
            }
        }
        FastTravelItems = new(defaultFastTravels);
        FilteredFastTravelItems = new(defaultFastTravels);
        FastTravelItems.CollectionChanged += (s, e) =>
        {
            ApplySearch();
            SaveFastTravelsCommand.Execute(null);
        };
    }

    protected override void OnActivated()
    {
        Messenger.Register<FastTravelViewModel, RemoveFastTravelMessage>(this, RemoveFastTravel);
        Messenger.Register<FastTravelViewModel, EditFastTravelMessage>(this, EditFastTravel);
    }

    private readonly ISettingsService _settings;
    private readonly IDialogService _dialogService;
    private readonly IRelayCommand<object> _travelCommand;
    private string _searchText = string.Empty;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
                ApplySearch();
        }
    }

    public IMapService MapService { get; }
    public FastTravelEditorViewModel Editor { get; }
    public ObservableCollection<IFastTravelItemViewModel> FastTravelItems { get; }
    public ObservableCollection<IFastTravelItemViewModel> FilteredFastTravelItems { get; }

    [RelayCommand]
    private void AddFastTravel()
    {
        if (!Editor.Travel.Validate())
            return;

        FastTravelItems.Add(new FastTravelItemViewModel(
            Editor.Travel.DescriptionName,
            Editor.Travel.MapName,
            Editor.Travel.Cell,
            Editor.Travel.Pad,
            _travelCommand));
    }

    [RelayCommand]
    private void SaveFastTravels()
    {
        StringCollection values = new();
        foreach (IFastTravelItemViewModel item in FastTravelItems)
            values.Add(item.ToString());
        _settings.Set("FastTravels", values);
    }

    [RelayCommand]
    private void ClearFastTravels()
    {
        if (_dialogService.ShowMessageBox("This will clear all fast travels from the list. Continue?", "Clear Fast Travels", true) == true)
            FastTravelItems.Clear();
    }

    private void ApplySearch()
    {
        string filter = SearchText.Trim();
        FilteredFastTravelItems.Clear();
        foreach (IFastTravelItemViewModel item in FastTravelItems)
        {
            if (FastTravelEntry.Matches(item.DescriptionName, item.MapName, item.Cell, filter))
            {
                FilteredFastTravelItems.Add(item);
            }
        }
    }

    private void RemoveFastTravel(FastTravelViewModel recipient, RemoveFastTravelMessage message)
    {
        if (message.FastTravel is null)
            return;

        recipient.FastTravelItems.Remove(message.FastTravel);
    }

    private void EditFastTravel(FastTravelViewModel recipient, EditFastTravelMessage message)
    {
        if (message.FastTravel is null)
            return;

        int index = recipient.FastTravelItems.IndexOf(message.FastTravel);
        FastTravelEditorDialogViewModel dialog = new(new(recipient.MapService, message.FastTravel));
        if (recipient._dialogService.ShowDialog(dialog) == true)
        {
            recipient.FastTravelItems[index] = dialog.Editor.Travel;
        }
    }
}