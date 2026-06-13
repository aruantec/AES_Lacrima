using SkiaSharp;
using System;
using System.Collections.Generic;

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

    private readonly Dictionary<object, Entry> _entries = new();

    public SKImage Register(object sourceKey, SKImage image)
    {
        if (_entries.TryGetValue(sourceKey, out var entry))
        {
            entry.RefCount++;
            return entry.Image;
        }

        _entries[sourceKey] = new Entry(image);
        return image;
    }

    public bool TryAcquire(object sourceKey, out SKImage image)
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

    public bool TryGetEntry(object sourceKey, out SKImage image, out int refCount)
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

    public void Release(object sourceKey, Action<SKImage> dispose)
    {
        if (!_entries.TryGetValue(sourceKey, out var entry))
            return;

        entry.RefCount--;
        if (entry.RefCount > 0)
            return;

        _entries.Remove(sourceKey);
        dispose(entry.Image);
    }

    public void Clear(Action<SKImage> dispose)
    {
        foreach (var entry in _entries.Values)
            dispose(entry.Image);
        _entries.Clear();
    }

    public IEnumerable<object> Keys => _entries.Keys;
}
