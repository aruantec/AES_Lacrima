using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AES_Controls.Composition;

/// <summary>
/// Reference-counted SKImage pool shared by carousel and grid layouts under <see cref="CompositionCoverControl"/>.
/// </summary>
internal sealed class CompositionSharedCoverCache
{
    private sealed class Entry
    {
        public Entry(SKImage image) => Image = image;

        public SKImage Image { get; }
        public int RefCount { get; set; } = 1;
    }

    private readonly object _sync = new();
    private readonly Dictionary<object, Entry> _entries = new();

    public SKImage Register(object sourceKey, SKImage image)
    {
        lock (_sync)
        {
            if (_entries.TryGetValue(sourceKey, out var entry))
            {
                entry.RefCount++;
                return entry.Image;
            }

            _entries[sourceKey] = new Entry(image);
            return image;
        }
    }

    /// <summary>
    /// Inserts a display image without claiming a consumer reference. Call <see cref="Acquire"/> per active slot.
    /// </summary>
    public SKImage RegisterUnretained(object sourceKey, SKImage image)
    {
        lock (_sync)
        {
            if (_entries.TryGetValue(sourceKey, out var entry))
                return entry.Image;

            _entries[sourceKey] = new Entry(image) { RefCount = 0 };
            return image;
        }
    }

    public bool TryPeek(object sourceKey, out SKImage image)
    {
        lock (_sync)
        {
            if (_entries.TryGetValue(sourceKey, out var entry))
            {
                image = entry.Image;
                return true;
            }

            image = null!;
            return false;
        }
    }

    public void Acquire(object sourceKey)
    {
        lock (_sync)
        {
            if (_entries.TryGetValue(sourceKey, out var entry))
                entry.RefCount++;
        }
    }

    public bool TryAcquire(object sourceKey, out SKImage image)
    {
        lock (_sync)
        {
            if (_entries.TryGetValue(sourceKey, out var entry))
            {
                entry.RefCount++;
                image = entry.Image;
                return true;
            }

            image = null!;
            return false;
        }
    }

    public bool TryGetEntry(object sourceKey, out SKImage image, out int refCount)
    {
        lock (_sync)
        {
            if (_entries.TryGetValue(sourceKey, out var entry))
            {
                image = entry.Image;
                refCount = entry.RefCount;
                return true;
            }

            image = null!;
            refCount = 0;
            return false;
        }
    }

    public void Release(object sourceKey, Action<SKImage> dispose)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(sourceKey, out var entry))
                return;

            entry.RefCount--;
            if (entry.RefCount > 0)
                return;

            _entries.Remove(sourceKey);
            dispose(entry.Image);
        }
    }

    /// <summary>
    /// Drops unreferenced entries when the cache grows too large.
    /// </summary>
    public int TrimUnreferenced(int maxEntries, Action<SKImage> dispose, Func<object, bool>? canTrimKey = null)
    {
        lock (_sync)
        {
            if (_entries.Count <= maxEntries)
                return 0;

            int trimmed = 0;
            foreach (var key in _entries.Keys.ToList())
            {
                if (_entries.Count <= maxEntries)
                    break;

                if (canTrimKey != null && !canTrimKey(key))
                    continue;

                if (!_entries.TryGetValue(key, out var entry) || entry.RefCount > 0)
                    continue;

                _entries.Remove(key);
                dispose(entry.Image);
                trimmed++;
            }

            return trimmed;
        }
    }

    public void Clear(Action<SKImage> dispose)
    {
        lock (_sync)
        {
            foreach (var entry in _entries.Values)
                dispose(entry.Image);
            _entries.Clear();
        }
    }

    public IEnumerable<object> Keys
    {
        get
        {
            lock (_sync)
                return _entries.Keys.ToArray();
        }
    }
}
