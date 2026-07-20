using ADB_Explorer.Helpers;
using ADB_Explorer.Models;

namespace ADB_Explorer.ViewModels;

public sealed class ExplorerState
{
    public string CurrentPath { get; internal set; }

    public string ParentPath { get; internal set; }

    public DriveViewModel CurrentDrive { get; internal set; }

    public Dictionary<string, string> CurrentDisplayNames { get; internal set; } = [];

    public ObservableList<TrashIndexer> RecycleIndex { get; internal set; } = [];

    public ObservableList<Package> Packages { get; internal set; } = [];

    public ObservableList<Package> VisiblePackages { get; } = [];

    public IEnumerable<FileClass> SelectedFiles { get; internal set; } = [];

    public IEnumerable<Package> SelectedPackages { get; internal set; } = [];
}
