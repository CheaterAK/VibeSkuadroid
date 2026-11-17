namespace Skua.App.Droid.Core.Engine;

/// <summary>A thread-safe bounded FIFO: adding beyond <see cref="Capacity"/> drops the oldest entry.</summary>
public sealed class RingBuffer<T>
{
    private readonly T[] _items;
    private readonly object _lock = new();
    private int _start;
    private int _count;

    public RingBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _items = new T[capacity];
    }

    public int Capacity => _items.Length;

    public int Count
    {
        get
        {
            lock (_lock)
                return _count;
        }
    }

    public void Add(T item)
    {
        lock (_lock)
        {
            int index = (_start + _count) % _items.Length;
            _items[index] = item;
            if (_count < _items.Length)
                _count++;
            else
                _start = (_start + 1) % _items.Length;
        }
    }

    /// <summary>The entries, oldest first.</summary>
    public IReadOnlyList<T> Snapshot()
    {
        lock (_lock)
        {
            T[] copy = new T[_count];
            for (int i = 0; i < _count; i++)
                copy[i] = _items[(_start + i) % _items.Length];
            return copy;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            Array.Clear(_items);
            _start = 0;
            _count = 0;
        }
    }
}
