namespace TalosDesk.Core.Processes;

internal sealed record PendingCommandOutput(Guid CommandId, long RunVersion, CommandOutput Output);

/// <summary>Bounds output waiting for a view without blocking the process output readers.</summary>
internal sealed class BoundedOutputInbox(int perCommandCapacity)
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, Queue<PendingCommandOutput>> _buffers = [];
    private readonly Queue<Guid> _readyCommands = new();
    private readonly HashSet<Guid> _readySet = [];

    public void Enqueue(Guid commandId, long runVersion, CommandOutput output)
    {
        if (perCommandCapacity < 1) throw new ArgumentOutOfRangeException(nameof(perCommandCapacity));
        lock (_sync)
        {
            if (!_buffers.TryGetValue(commandId, out var buffer)) _buffers[commandId] = buffer = new Queue<PendingCommandOutput>();
            if (buffer.Count == perCommandCapacity) buffer.Dequeue();
            buffer.Enqueue(new PendingCommandOutput(commandId, runVersion, output));
            if (_readySet.Add(commandId)) _readyCommands.Enqueue(commandId);
        }
    }

    public IReadOnlyList<PendingCommandOutput> Take(int maxCount)
    {
        if (maxCount < 1) throw new ArgumentOutOfRangeException(nameof(maxCount));
        var batch = new List<PendingCommandOutput>(maxCount);
        lock (_sync)
        {
            while (batch.Count < maxCount && _readyCommands.Count > 0)
            {
                var commandId = _readyCommands.Dequeue();
                if (!_buffers.TryGetValue(commandId, out var buffer) || buffer.Count == 0) continue;

                batch.Add(buffer.Dequeue());
                if (buffer.Count > 0) _readyCommands.Enqueue(commandId);
                else
                {
                    _buffers.Remove(commandId);
                    _readySet.Remove(commandId);
                }
            }
        }

        return batch;
    }

    public bool HasPending
    {
        get { lock (_sync) return _buffers.Count > 0; }
    }

    public void Clear(Guid commandId)
    {
        lock (_sync)
        {
            _buffers.Remove(commandId);
            _readySet.Remove(commandId);
            if (_readyCommands.Count == 0) return;
            var retained = _readyCommands.Where(readyId => readyId != commandId).ToArray();
            _readyCommands.Clear();
            foreach (var readyId in retained) _readyCommands.Enqueue(readyId);
        }
    }
}
