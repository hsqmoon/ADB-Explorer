namespace ADB_Explorer.Helpers;

public class BatchObservableCollection<T> : ObservableCollection<T>
{
    private bool suppressOnCollectionChanged;

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (!suppressOnCollectionChanged)
            base.OnCollectionChanged(e);
    }

    public void AddRange(IEnumerable<T> items)
    {
        var itemList = items?.ToList() ?? [];
        if (itemList.Count == 0)
            return;

        if (itemList.Count == 1)
        {
            Add(itemList[0]);
            return;
        }

        suppressOnCollectionChanged = true;
        foreach (var item in itemList)
        {
            Add(item);
        }
        suppressOnCollectionChanged = false;

        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    public void RemoveRange(int index, int count)
    {
        if (count <= 0 || index < 0 || index >= Count)
            return;

        int actualCount = Math.Min(count, Count - index);
        if (actualCount == 1)
        {
            RemoveAt(index);
            return;
        }

        suppressOnCollectionChanged = true;
        for (int i = 0; i < actualCount; i++)
        {
            RemoveAt(index);
        }
        suppressOnCollectionChanged = false;

        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
