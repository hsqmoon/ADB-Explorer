using System;

namespace ADB_Explorer.Helpers;

public class ObservableList<T> : ObservableCollection<T> where T : INotifyPropertyChanged
{
    private bool suppressOnCollectionChanged = false;

    public ObservableList()
    { }

    public ObservableList(IEnumerable<T> items) : base(items)
    { }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (!suppressOnCollectionChanged)
            base.OnCollectionChanged(e);
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (!suppressOnCollectionChanged)
            base.OnPropertyChanged(e);
    }

    private void NotifyReset()
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// <summary>
    /// Adds a collection of items to the end of the list.
    /// </summary>
    /// <param name="items"></param>
    public void AddRange(IEnumerable<T> items)
    {
        var itemsList = items.ToArray();
        switch (itemsList.Length)
        {
            case < 1:
                return;
            case < 2:
                // When adding one item, we can skip the notification suppression mechanism
                Add(itemsList[0]);
                return;
        }

        // When adding more than one item, we suppress the notification mechanism while items are being added
        suppressOnCollectionChanged = true;
        try
        {
            foreach (T item in itemsList)
                Items.Add(item);
        }
        finally
        {
            suppressOnCollectionChanged = false;
            NotifyReset();
        }
    }

    public void RemoveAll()
    {
        if (Count == 0)
            return;

        suppressOnCollectionChanged = true;
        try
        {
            Items.Clear();
        }
        finally
        {
            suppressOnCollectionChanged = false;
            NotifyReset();
        }
    }

    public T Find(Func<T, bool> predicate)
    {
        if (Count == 0 || predicate is null)
            return default;

        foreach (var item in this)
        {
            if (predicate(item))
                return item;
        }

        return default;
    }

    /// <summary>
    /// Removes all items that match the predicate.
    /// </summary>
    /// <param name="predicate"></param>
    /// <returns><see langword="true"/> if at least one item was removed, otherwise <see langword="false"/></returns>
    public bool RemoveAll(Func<T, bool> predicate)
    {
        var resultList = this.Where(predicate).ToArray();
        switch (resultList.Length)
        {
            case < 1:
                return false;
            case 1:
                // When removing one item, we can skip the notification suppression mechanism
                Remove(resultList[0]);
                return true;
        }

        var remainingItems = this.Except(resultList).ToArray();
        suppressOnCollectionChanged = true;
        try
        {
            Items.Clear();
            foreach (T item in remainingItems)
                Items.Add(item);
        }
        finally
        {
            suppressOnCollectionChanged = false;
            NotifyReset();
        }

        return true;

    }

    public void RemoveAll(IEnumerable<T> items)
    {
        var itemsToRemove = items.ToHashSet();
        var resultList = this.Where(itemsToRemove.Contains).ToArray();
        switch (resultList.Length)
        {
            case < 1:
                return;
            case 1:
                // When removing one item, we can skip the notification suppression mechanism
                Remove(resultList[0]);
                return;
        }

        var remainingItems = this.Where(item => !itemsToRemove.Contains(item)).ToArray();
        suppressOnCollectionChanged = true;
        try
        {
            Items.Clear();
            foreach (T item in remainingItems)
                Items.Add(item);
        }
        finally
        {
            suppressOnCollectionChanged = false;
            NotifyReset();
        }
    }

    public void ForEach(Action<T> action)
    {
        foreach (var item in this)
            action(item);
    }
}
