namespace UniGetUI.Core.Classes;

/// <summary>
/// Decides which entries of a cache to drop. Keys are either <em>pinned</em>, and kept until
/// <see cref="Clear"/>, or <em>evictable</em>, and kept only while they are among the
/// <see cref="Capacity"/> most recently used evictable keys. The tracker holds no values: callers
/// remove the keys it returns from their own stores. Pinning a key that is already tracked as
/// evictable promotes it; an evictable use of a pinned key leaves it pinned. Thread-safe.
/// </summary>
public sealed class PinnedLruKeyTracker<TKey>
    where TKey : notnull
{
    private readonly Lock _lock = new();
    private readonly HashSet<TKey> _pinned = [];
    private readonly Dictionary<TKey, LinkedListNode<TKey>> _evictable = [];
    private readonly LinkedList<TKey> _order = new(); // most recently used first

    public PinnedLruKeyTracker(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
    }

    /// <summary>The maximum number of evictable keys kept at once.</summary>
    public int Capacity { get; }

    public int PinnedCount
    {
        get
        {
            lock (_lock)
                return _pinned.Count;
        }
    }

    public int EvictableCount
    {
        get
        {
            lock (_lock)
                return _evictable.Count;
        }
    }

    /// <summary>Keeps <paramref name="key"/> until <see cref="Clear"/>.</summary>
    public void Pin(TKey key) => Pin(key, null);

    /// <summary>
    /// Keeps <paramref name="key"/> until <see cref="Clear"/>, and runs <paramref name="store"/>
    /// (which writes the key's value to the caller's cache) before any other change to the
    /// tracker can be made. See <see cref="Touch(TKey, Action?, Action{TKey})"/>.
    /// </summary>
    public void Pin(TKey key, Action? store)
    {
        lock (_lock)
        {
            if (_evictable.Remove(key, out LinkedListNode<TKey>? node))
                _order.Remove(node);
            _pinned.Add(key);
            store?.Invoke();
        }
    }

    /// <summary>
    /// Records a use of <paramref name="key"/> like <see cref="Touch(TKey)"/>, with the caller's
    /// cache kept in step: <paramref name="store"/> writes the key's value and
    /// <paramref name="evict"/> removes each key that fell out, all before any other change to
    /// the tracker can be made. Without this, a key chosen for eviction could be pinned and
    /// stored by another thread before its value was removed, leaving it pinned with no value.
    /// The callbacks run inside the tracker's lock: they must be quick and must not call back
    /// into the tracker.
    /// </summary>
    public void Touch(TKey key, Action? store, Action<TKey> evict)
    {
        ArgumentNullException.ThrowIfNull(evict);
        lock (_lock)
        {
            store?.Invoke();
            foreach (TKey evicted in Touch(key))
                evict(evicted);
        }
    }

    /// <summary>
    /// Records a use of <paramref name="key"/>. Unless it is pinned, it becomes the most
    /// recently used evictable key.
    /// </summary>
    /// <returns>The keys that fell out of the cache and should be removed from it.</returns>
    public IReadOnlyList<TKey> Touch(TKey key)
    {
        lock (_lock)
        {
            if (_pinned.Contains(key))
                return [];

            if (_evictable.TryGetValue(key, out LinkedListNode<TKey>? node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                return [];
            }

            _evictable[key] = _order.AddFirst(key);
            if (_evictable.Count <= Capacity)
                return [];

            List<TKey> evicted = [];
            while (_evictable.Count > Capacity && _order.Last is { } oldest)
            {
                _order.RemoveLast();
                _evictable.Remove(oldest.Value);
                evicted.Add(oldest.Value);
            }
            return evicted;
        }
    }

    /// <summary>Records a use of <paramref name="key"/> only if it is already tracked.</summary>
    public void Refresh(TKey key)
    {
        lock (_lock)
        {
            if (_evictable.TryGetValue(key, out LinkedListNode<TKey>? node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
            }
        }
    }

    public void Clear() => Clear(null);

    /// <summary>
    /// Forgets every key, and runs <paramref name="clearStore"/> (which empties the caller's
    /// cache) before any other change to the tracker can be made.
    /// </summary>
    public void Clear(Action? clearStore)
    {
        lock (_lock)
        {
            _pinned.Clear();
            _evictable.Clear();
            _order.Clear();
            clearStore?.Invoke();
        }
    }
}
