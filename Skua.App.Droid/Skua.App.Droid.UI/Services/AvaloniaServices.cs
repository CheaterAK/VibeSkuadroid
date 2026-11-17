using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Skua.App.Droid.Core.Platform;

namespace Skua.App.Droid.UI.Services;

/// <summary>Posts to Avalonia's UI thread.</summary>
public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public void Post(Action action) => Dispatcher.UIThread.Post(action);
}

/// <summary>
/// File/folder pickers and clipboard through the current view's TopLevel. On Android the storage provider is the
/// Storage Access Framework (ACTION_OPEN_DOCUMENT / ACTION_OPEN_DOCUMENT_TREE): the app gets read access to what
/// the user picked and needs no storage permission.
/// </summary>
public sealed class AvaloniaFilePicker : IFilePicker
{
    /// <summary>Bounds a folder import (the community repository has a few thousand files).</summary>
    public const int MaxFolderFiles = 10_000;

    private const int MaxDepth = 12;

    /// <summary>The TopLevel of the view currently shown; updated by MainView when it is attached.</summary>
    public static TopLevel? CurrentTopLevel { get; set; }

    public async Task<IReadOnlyList<PickedFile>> PickFilesAsync()
    {
        IStorageProvider storage = RequireTopLevel().StorageProvider;
        IReadOnlyList<IStorageFile> files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import scripts",
            AllowMultiple = true,
        });
        List<PickedFile> result = new();
        foreach (IStorageFile file in files)
            result.Add(await ToPickedAsync(file, file.Name));
        return result;
    }

    public async Task<IReadOnlyList<PickedFile>> PickFolderAsync()
    {
        IStorageProvider storage = RequireTopLevel().StorageProvider;
        IReadOnlyList<IStorageFolder> folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Import a script folder",
            AllowMultiple = false,
        });
        List<PickedFile> result = new();
        foreach (IStorageFolder folder in folders)
            await CollectAsync(folder, folder.Name, 0, result);
        return result;
    }

    public async Task CopyTextAsync(string text)
    {
        IClipboard? clipboard = RequireTopLevel().Clipboard;
        if (clipboard is not null)
            await clipboard.SetTextAsync(text);
    }

    private static async Task CollectAsync(IStorageFolder folder, string prefix, int depth, List<PickedFile> result)
    {
        if (depth > MaxDepth)
            return;
        await foreach (IStorageItem item in folder.GetItemsAsync())
        {
            if (result.Count >= MaxFolderFiles)
                return;
            string path = prefix + "/" + item.Name;
            if (item is IStorageFolder sub)
                await CollectAsync(sub, path, depth + 1, result);
            else if (item is IStorageFile file)
                result.Add(await ToPickedAsync(file, path));
        }
    }

    private static async Task<PickedFile> ToPickedAsync(IStorageFile file, string relativePath)
    {
        long? size = null;
        try
        {
            size = (long?)(await file.GetBasicPropertiesAsync()).Size;
        }
        catch (Exception e) when (e is IOException or NotSupportedException or UnauthorizedAccessException)
        {
        }
        return new PickedFile(relativePath, size, () => file.OpenReadAsync());
    }

    private static TopLevel RequireTopLevel() =>
        CurrentTopLevel ?? throw new InvalidOperationException("No window is showing.");
}
