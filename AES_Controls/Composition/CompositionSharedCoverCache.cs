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
