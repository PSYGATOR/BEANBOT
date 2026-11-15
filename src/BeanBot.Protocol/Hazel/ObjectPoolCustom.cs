using System.Collections.Concurrent;

namespace BeanBot.Protocol.Hazel;

/// <summary>
/// Thread-safe object pool for recyclable Hazel objects.
/// Source: Impostor.Hazel.ObjectPoolCustom.
/// </summary>
public sealed class ObjectPoolCustom<T> where T : class, IRecyclable
{
    private readonly List<T> _pool = new();
    private readonly ConcurrentDictionary<T, bool> _inUse = new();
    private readonly Func<T> _objectFactory;
    private int _numberCreated;

    internal ObjectPoolCustom(Func<T> objectFactory)
    {
        _objectFactory = objectFactory;
    }

    public int NumberCreated => _numberCreated;
    public int NumberInUse => _inUse.Count;
    public int NumberNotInUse
    {
        get
        {
            lock (_pool)
            {
                return _pool.Count;
            }
        }
    }

    internal T GetObject()
    {
        T item;
        lock (_pool)
        {
            if (_pool.Count > 0)
            {
                var idx = _pool.Count - 1;
                item = _pool[idx];
                _pool.RemoveAt(idx);
            }
            else
            {
                Interlocked.Increment(ref _numberCreated);
                item = _objectFactory();
            }
        }

        if (!_inUse.TryAdd(item, true))
        {
            throw new InvalidOperationException($"Duplicate pull for {typeof(T).Name}");
        }

        return item;
    }

    internal void PutObject(T item)
    {
        if (_inUse.TryRemove(item, out _))
        {
            lock (_pool)
            {
                _pool.Add(item);
            }
        }
    }
}
