namespace Skua.App.Droid.Core.Engine;

/// <summary>
/// A thread-safe hand-over queue from producer threads to the UI that never grows past <see cref="Capacity"/>: adding
/// beyond it drops the oldest item and marks the feed as overflowed, so the consumer knows to rebuild from the source
/// of truth instead of replaying what it got.
/// </summary>
public sealed class BoundedFeed<T>
{
    private readonly Queue<T> _items = new();
    private readonly object _lock = new();
    private bool _overflowed;

    public BoundedFeed(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
    }

    public int Capacity { get; }

    public int Count
    {
        get
        {
            lock (_lock)
                return _items.Count;
        }
    }

    public void Add(T item)
    {
        lock (_lock)
        {
            if (_items.Count >= Capacity)
            {
                _items.Dequeue();
                _overflowed = true;
            }
            _items.Enqueue(item);
        }
    }

    /// <summary>Takes everything queued, oldest first, and whether items were dropped since the last drain.</summary>
    public (IReadOnlyList<T> Items, bool Overflowed) Drain()
    {
        lock (_lock)
        {
            T[] items = _items.ToArray();
            bool overflowed = _overflowed;
            _items.Clear();
            _overflowed = false;
            return (items, overflowed);
        }
    }
}
