using ADB_Explorer.Helpers;
using ADB_Explorer.Models;
using ADB_Explorer.Services;
using ADB_Explorer.ViewModels;

namespace ADB_Explorer.Controls;

/// <summary>
/// Interaction logic for NavigationBox.xaml
/// </summary>
public partial class NavigationBox
{
    private readonly SavedLocation currentLocationPlaceholder = new();
    private long pathUpdateVersion;

    public enum ViewMode
    {
        None,
        Breadcrumbs,
        Path,
    }

    public NavigationBox()
    {
        InitializeComponent();

        Breadcrumbs = [];
        Focusable = true;

        Mode = ViewMode.None;
        UpdateSavedItems();

        SizeChanged += (sender, args) => ScheduleBreadcrumbApply();

        App.RuntimeSettings.PropertyChanged += (sender, args) =>
        {
            if (args.PropertyName == nameof(AppRuntimeSettings.SavedLocations))
            {
                UpdateSavedItems();
            }
        };
    }

    internal void CloseSavedItemsFlyout() => FlyoutService.GetFlyout(SavedItemsButton).Hide();

    internal void ShowPath(string path)
    {
        Path = path;
        Mode = ViewMode.Breadcrumbs;
    }

    #region Dependency Properties

    public string Path
    {
        get => (string)GetValue(PathProperty);
        set
        {
            bool update = Path != value;

            SetValue(PathProperty, value);
            
            if (update)
            {
                IsFUSE = DriveHelper.GetCurrentDrive(value)?.IsFUSE is true;
                SchedulePathUpdate(value);
            }
        }
    }

    public static readonly DependencyProperty PathProperty =
        DependencyProperty.Register(nameof(Path), typeof(string),
          typeof(NavigationBox), new PropertyMetadata(null));

    public string DisplayPath
    {
        get => (string)GetValue(DisplayPathProperty);
        set => SetValue(DisplayPathProperty, value);
    }

    public static readonly DependencyProperty DisplayPathProperty =
        DependencyProperty.Register(nameof(DisplayPath), typeof(string),
          typeof(NavigationBox), new PropertyMetadata(null));

    public List<MenuItem> Breadcrumbs
    {
        get => (List<MenuItem>)GetValue(BreadcrumbsProperty);
        set => SetValue(BreadcrumbsProperty, value);
    }

    public static readonly DependencyProperty BreadcrumbsProperty =
        DependencyProperty.Register(nameof(Breadcrumbs), typeof(List<MenuItem>),
          typeof(NavigationBox), new PropertyMetadata(null));

    public bool IsFUSE
    {
        get => (bool)GetValue(IsFUSEProperty);
        set => SetValue(IsFUSEProperty, value);
    }

    public static readonly DependencyProperty IsFUSEProperty =
        DependencyProperty.Register(nameof(IsFUSE), typeof(bool),
          typeof(NavigationBox), new PropertyMetadata(false));

    public bool IsLoadingProgressVisible
    {
        get => (bool)GetValue(IsLoadingProgressVisibleProperty);
        set => SetValue(IsLoadingProgressVisibleProperty, value);
    }

    public static readonly DependencyProperty IsLoadingProgressVisibleProperty =
        DependencyProperty.Register(nameof(IsLoadingProgressVisible), typeof(bool),
          typeof(NavigationBox), new PropertyMetadata(false));

    public Thickness MenuPadding
    {
        get => (Thickness)GetValue(MenuPaddingProperty);
        set => SetValue(MenuPaddingProperty, value);
    }

    public static readonly DependencyProperty MenuPaddingProperty =
        DependencyProperty.Register(nameof(MenuPadding), typeof(Thickness),
          typeof(NavigationBox), new PropertyMetadata(null));

    public ObservableList<IMenuItem> Items
    {
        get => (ObservableList<IMenuItem>)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public static readonly DependencyProperty ItemsProperty =
        DependencyProperty.Register(nameof(Items), typeof(ObservableList<IMenuItem>),
          typeof(NavigationBox), new PropertyMetadata(null));

    public ObservableList<SavedLocation> SavedItems
    {
        get => (ObservableList<SavedLocation>)GetValue(SavedItemsProperty);
        set => SetValue(SavedItemsProperty, value);
    }

    public static readonly DependencyProperty SavedItemsProperty =
        DependencyProperty.Register(nameof(SavedItems), typeof(ObservableList<SavedLocation>),
          typeof(NavigationBox), new PropertyMetadata(null));

    public bool IsCurrentSaved
    {
        get => (bool)GetValue(IsCurrentSavedProperty);
        set => SetValue(IsCurrentSavedProperty, value);
    }

    public static readonly DependencyProperty IsCurrentSavedProperty =
        DependencyProperty.Register(nameof(IsCurrentSaved), typeof(bool),
          typeof(NavigationBox), new PropertyMetadata(false));

    #endregion

    public ViewMode Mode
    {
        get => (ViewMode)GetValue(ModeProperty);
        set
        {
            SetValue(ModeProperty, value);

            if (value is ViewMode.Path)
                PathBox.Focus();
            else if (PathBox.IsFocused)
                Focus();
        }
    }

    public static readonly DependencyProperty ModeProperty =
        DependencyProperty.Register(nameof(Mode), typeof(ViewMode),
          typeof(NavigationBox), new PropertyMetadata(ViewMode.None));

    public double MenuHeight => Height - MenuPadding.Top - MenuPadding.Bottom;

    private void SchedulePathUpdate(string path)
    {
        long version = Interlocked.Increment(ref pathUpdateVersion);
        if (string.IsNullOrEmpty(path))
        {
            void clear()
            {
                if (version != Volatile.Read(ref pathUpdateVersion))
                    return;

                locations = [];
                breadcrumbs = [];
                itemWidths = [];
                Items = [];
                UpdateCurrentSavedState();
            }

            if (Application.Current is App currentApp)
                currentApp.EnqueueUiLatest("navigation.breadcrumbs.clear", "navigation.breadcrumbs.clear", clear);
            else
                clear();
            return;
        }

        if (Application.Current is not App app)
        {
            PreparePath(path);
            ArrangeBreadcrumbs();
            UpdateCurrentSavedState();
            return;
        }

        app.EnqueueUiLatest(
            "navigation.breadcrumbs.prepare",
            "navigation.breadcrumbs.prepare",
            () =>
            {
                if (version != Volatile.Read(ref pathUpdateVersion))
                    return;

                PreparePath(path);
                app.EnqueueUiLatest(
                    "navigation.breadcrumbs.apply",
                    "navigation.breadcrumbs.apply",
                    () =>
                    {
                        if (version == Volatile.Read(ref pathUpdateVersion))
                            ArrangeBreadcrumbs();
                    });
                app.EnqueueUiLatest(
                    "navigation.saved-location",
                    "navigation.saved-location",
                    () =>
                    {
                        if (version == Volatile.Read(ref pathUpdateVersion))
                            UpdateCurrentSavedState();
                    });
            });
    }

    private void PreparePath(string path)
    {
        var driveView = AdbLocation.StringFromLocation(Navigation.SpecialLocation.DriveView);
        if (path == driveView)
            PrepareBreadcrumbs(path);
        else
            PrepareBreadcrumbs(driveView + path);
    }

    private void UpdateSavedItems()
    {
        SavedItems = App.RuntimeSettings.SavedLocations is null
            ? []
            : [.. App.RuntimeSettings.SavedLocations.Select(p => new SavedLocation(p))];

        UpdateCurrentSavedState();
        ((ItemsControl)Resources["SavedItemsControl"]).ItemsSource = SavedItems;
    }

    private void UpdateCurrentSavedState()
    {
        IsCurrentSaved = SavedItems.Any(i =>
            !ReferenceEquals(i, currentLocationPlaceholder) && i.Path == Path);

        bool showPlaceholder = AdbLocation.LocationFromString(Path) is Navigation.SpecialLocation.None
            && !IsCurrentSaved;
        bool containsPlaceholder = SavedItems.Contains(currentLocationPlaceholder);
        if (showPlaceholder && !containsPlaceholder)
            SavedItems.Insert(0, currentLocationPlaceholder);
        else if (!showPlaceholder && containsPlaceholder)
            SavedItems.Remove(currentLocationPlaceholder);
    }

    public void Refresh() => SchedulePathUpdate(Path);

    public static IEnumerable<AdbLocation> SeparatePath(string path)
    {
        string current = path;

        var driveView = AdbLocation.StringFromLocation(Navigation.SpecialLocation.DriveView);
        if (path.StartsWith(driveView))
        {
            yield return new(Navigation.SpecialLocation.DriveView);
            current = current[driveView.Length..];
        }

        if (current.Length == 0)
            yield break;

        var pairs = App.ExplorerState.CurrentDisplayNames.Where(kv => current.StartsWith(kv.Key));
        var drive = pairs.Count() > 1
            ? pairs.OrderBy(kv => kv.Key.Length).Last()
            : pairs.FirstOrDefault();

        yield return new(drive.Key);

        if (current.Length == 0)
            yield break;

        var index = drive.Key.Length;

        if (current.Length == index)
            yield break;

        while (index >= 0)
        {
            if (current.Length <= index)
                break;

            var next = current.IndexOf('/', index + 1);

            yield return new(current[..(next < 0 ? ^0 : next)]);

            index = next;
        }
    }

    List<AdbLocation> locations = [];
    List<TextMenu> breadcrumbs = [];
    List<double> itemWidths = [];

    private void PrepareBreadcrumbs(string path)
    {
        if (string.IsNullOrEmpty(path))
            return;

        locations = SeparatePath(path).ToList();
        breadcrumbs = locations.Select(item => item.NameSubMenu).ToList();
        breadcrumbs[^1].IsLast = true;

        var typeface = new Typeface(
            SystemFonts.MessageFontFamily,
            FontStyles.Normal,
            FontWeights.Normal,
            FontStretches.Normal);
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        itemWidths = [.. breadcrumbs.Select(item =>
            new FormattedText(
                item.Action.Description ?? "",
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                typeface,
                SystemFonts.MessageFontSize,
                Brushes.Black,
                pixelsPerDip).WidthIncludingTrailingWhitespace + 44)];

    }

    private void ScheduleBreadcrumbApply()
    {
        long version = Volatile.Read(ref pathUpdateVersion);
        if (Application.Current is App app)
        {
            app.EnqueueUiLatest(
                "navigation.breadcrumbs.apply",
                "navigation.breadcrumbs.apply",
                () =>
                {
                    if (version == Volatile.Read(ref pathUpdateVersion))
                        ArrangeBreadcrumbs();
                });
        }
        else
        {
            ArrangeBreadcrumbs();
        }
    }

    private void ArrangeBreadcrumbs()
    {
        if (breadcrumbs.Count == 0)
            return;

        int lastHiddenIndex = -1;
        double trailingWidth = itemWidths.Skip(1).Sum();
        for (var i = 1; i < breadcrumbs.Count; i++)
        {
            if (125 + itemWidths[0] + trailingWidth > PathBox.ActualWidth)
                lastHiddenIndex = i;

            trailingWidth -= itemWidths[i];
        }

        if (lastHiddenIndex == -1)
            Items = [.. breadcrumbs];
        else
        {
            var excessButton = new TextMenu(
                new FileAction(FileAction.FileActionType.None, () => true, () => { }, "\uE712"))
            {
                Children = locations[1..(lastHiddenIndex + 1)].Select(item => item.ExcessSubMenu)
            };

            var itemsControl = (ItemsControl)Resources["OverflowItemsControl"];
            itemsControl.ItemsSource = excessButton.Children;

            Items = [breadcrumbs[0], excessButton, .. breadcrumbs[(lastHiddenIndex + 1)..]];
        }
    }

    private void PathBox_GotFocus(object sender, RoutedEventArgs e)
    {
        Mode = ViewMode.Path;

        DisplayPath = AdbLocation.LocationFromString(Path) is Navigation.SpecialLocation.None ? Path : "";

        PathBox.SelectAll();
    }

    private void PathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape || (e.Key == Key.Enter && PathBox.Text == ""))
        {
            e.Handled = true;
            Mode = ViewMode.Breadcrumbs;
        }
        else if (e.Key == Key.Enter)
        {
            ((App)Application.Current).RequestPathNavigation(AdbExplorerConst.POSSIBLE_RECYCLE_PATHS.Any(DisplayPath.StartsWith)
                ? AdbExplorerConst.RECYCLE_PATH
                : DisplayPath);

            e.Handled = true;
            Mode = ViewMode.Breadcrumbs;
        }
    }

    private void PathBox_LostFocus(object sender, RoutedEventArgs e)
    {
        Mode = ViewMode.Breadcrumbs;
    }

    private void FuseIcon_Click(object sender, RoutedEventArgs e)
    {
        ((ToolTip)FuseIcon.ToolTip).IsOpen = true;
    }

    private void FuseIcon_MouseLeave(object sender, MouseEventArgs e)
    {
        ((ToolTip)FuseIcon.ToolTip).IsOpen = false;
    }
}
