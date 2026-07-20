using ADB_Explorer.Helpers;
using ADB_Explorer.Models;
using ADB_Explorer.Services;

namespace ADB_Explorer.ViewModels;

public class SavedLocation : ViewModelBase
{
    private string path;
    public string Path
    {
        get => path;
        set => Set(ref path, value);
    }

    public BaseAction DeleteAction { get; }

    public BaseAction AddAction { get; }

    public BaseAction NavigateAction { get; }

    public SavedLocation(string path = "")
    {
        Path = path;

        DeleteAction = new(
            () => !string.IsNullOrEmpty(Path),
            () =>
            {
                App.RuntimeSettings.SavedLocations = [.. App.RuntimeSettings.SavedLocations.Except([Path])];
                Storage.StoreValue(nameof(App.RuntimeSettings.SavedLocations), App.RuntimeSettings.SavedLocations.ToArray());
            });

        AddAction = new(
            () => string.IsNullOrEmpty(Path),
            () =>
            {
                if (App.RuntimeSettings.SavedLocations is null)
                    App.RuntimeSettings.SavedLocations = [App.ExplorerState.CurrentPath];
                else
                    App.RuntimeSettings.SavedLocations = [.. App.RuntimeSettings.SavedLocations, App.ExplorerState.CurrentPath];

                Storage.StoreValue(nameof(App.RuntimeSettings.SavedLocations), App.RuntimeSettings.SavedLocations.ToArray());
            });

        NavigateAction = new(
            () => !string.IsNullOrEmpty(Path),
            () => (Application.Current as App)?.RequestNavigation(new(Path)));
    }
}
