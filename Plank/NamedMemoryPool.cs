using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace Plank;

public sealed class NamedMemoryPool : IBufferPool
{
    readonly Dictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);

    public void Register(string name, int bufferLength, int initialCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (bufferLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(bufferLength), bufferLength, "Buffer length must be positive.");
        if (initialCount < 0)
            throw new ArgumentOutOfRangeException(nameof(initialCount), initialCount, "Initial count must be non-negative.");

        if (_buckets.TryGetValue(name, out var existing))
        {
            existing.Preload(bufferLength, initialCount);
            return;
        }

        _buckets[name] = new Bucket(bufferLength, initialCount);
    }

    public IMemoryOwner<byte> Rent(string name, int minimumLength)
        => Rent(name, minimumLength, 0);

    public IMemoryOwner<byte> Rent(string name, int minimumLength, long requestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (minimumLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(minimumLength), minimumLength, "Minimum length must be positive.");
        if (requestId < 0)
            throw new ArgumentOutOfRangeException(nameof(requestId), requestId, "Request id must be non-negative.");
        if (!_buckets.TryGetValue(name, out var bucket))
            throw new InvalidOperationException($"Buffer pool bucket '{name}' is not registered.");

        return bucket.Rent(minimumLength, requestId);
    }

    sealed class Bucket
    {
        readonly object _sync = new();
        readonly List<Waiter> _waiters = [];
        BufferLease[] _items;
        int _count;
        int _bufferLength;
        long _nextWaiterSequence;

        internal Bucket(int bufferLength, int initialCount)
        {
            _bufferLength = bufferLength;
            _items = initialCount > 0 ? new BufferLease[initialCount] : [];
            _count = 0;
            for (var i = 0; i < initialCount; i++)
                _items[_count++] = new BufferLease(this, new byte[_bufferLength]);
        }

        internal BufferLease Rent(int minimumLength, long requestId)
        {
            lock (_sync)
            {
                if (minimumLength > _bufferLength)
                    throw new InvalidOperationException($"Requested {minimumLength} bytes but bucket capacity is {_bufferLength}.");
                if (_count == 0)
                {
                    try
                    {
                        var created = new BufferLease(this, new byte[_bufferLength]);
                        created.MarkRented();
                        return created;
                    }
                    catch (OutOfMemoryException)
                    {
                        var waiter = new Waiter(requestId, _nextWaiterSequence++);
                        _waiters.Add(waiter);
                        while (true)
                        {
                            Monitor.Wait(_sync);
                            if (waiter.Lease is not null)
                                return waiter.Lease;
                        }
                    }
                }

                _count--;
                var buffer = _items[_count];
                _items[_count] = null!;
                buffer.MarkRented();
                return buffer;
            }
        }

        internal void Return(BufferLease lease)
        {
            lock (_sync)
            {
                if (lease.Length < _bufferLength)
                    throw new InvalidOperationException($"Returned buffer length {lease.Length} does not match bucket capacity {_bufferLength}.");
                if (_waiters.Count > 0)
                {
                    var index = GetNextWaiterIndex();
                    var waiter = _waiters[index];
                    _waiters.RemoveAt(index);
                    lease.MarkRented();
                    waiter.Lease = lease;
                    Monitor.PulseAll(_sync);
                    return;
                }
                if (_count == _items.Length)
                    return;

                _items[_count++] = lease;
            }
        }

        internal void Preload(int bufferLength, int initialCount)
        {
            lock (_sync)
            {
                if (bufferLength > _bufferLength)
                    throw new InvalidOperationException($"Bucket was registered at {_bufferLength} bytes and cannot grow to {bufferLength}.");

                var freeSlots = _items.Length - _count;
                var toAdd = initialCount - freeSlots;
                if (toAdd <= 0)
                    return;

                var capped = Math.Min(toAdd, _items.Length == 0 ? initialCount : _items.Length);
                if (capped <= 0)
                    return;

                var grown = new BufferLease[_items.Length + capped];
                Array.Copy(_items, grown, _items.Length);
                _items = grown;
                for (var i = 0; i < capped; i++)
                    _items[_count++] = new BufferLease(this, new byte[_bufferLength]);
            }
        }

        int GetNextWaiterIndex()
        {
            var selectedIndex = 0;
            var selected = _waiters[0];
            for (var i = 1; i < _waiters.Count; i++)
            {
                var candidate = _waiters[i];
                if (candidate.RequestId < selected.RequestId)
                {
                    selected = candidate;
                    selectedIndex = i;
                    continue;
                }

                if (candidate.RequestId != selected.RequestId)
                    continue;
                if (candidate.Sequence >= selected.Sequence)
                    continue;
                selected = candidate;
                selectedIndex = i;
            }

            return selectedIndex;
        }

        sealed class Waiter
        {
            internal readonly long RequestId;
            internal readonly long Sequence;
            internal BufferLease? Lease;

            internal Waiter(long requestId, long sequence)
            {
                RequestId = requestId;
                Sequence = sequence;
            }
        }
    }

    sealed class BufferLease : IMemoryOwner<byte>
    {
        readonly Bucket _owner;
        readonly byte[] _buffer;
        int _isRented;

        internal BufferLease(Bucket owner, byte[] buffer)
        {
            _owner = owner;
            _buffer = buffer;
            _isRented = 0;
        }

        internal int Length
            => _buffer.Length;

        public Memory<byte> Memory
        {
            get
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _isRented) == 0, this);

                return _buffer;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _isRented, 0) == 0)
                return;

            _owner.Return(this);
        }

        internal void MarkRented()
        {
            if (Interlocked.Exchange(ref _isRented, 1) != 0)
                throw new InvalidOperationException("Buffer lease is already rented.");
        }
    }
}
