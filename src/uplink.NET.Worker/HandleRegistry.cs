namespace uplink.NET.Worker;

/// <summary>Thread-safe registry mapping long IDs to native handles (nint).</summary>
internal sealed class HandleRegistry
{
    private long _nextId = 1000;
    private readonly Dictionary<long, nint> _handles = new();

    internal long Register(nint handle)
    {
        long id = Interlocked.Increment(ref _nextId);
        lock (_handles)
            _handles[id] = handle;
        return id;
    }

    internal nint Get(long id)
    {
        lock (_handles)
        {
            if (!_handles.TryGetValue(id, out var h))
                throw new KeyNotFoundException($"Handle {id} not found in registry.");
            return h;
        }
    }

    internal bool TryGet(long id, out nint handle)
    {
        lock (_handles)
            return _handles.TryGetValue(id, out handle);
    }

    internal bool Remove(long id, out nint handle)
    {
        lock (_handles)
            return _handles.Remove(id, out handle);
    }
}
