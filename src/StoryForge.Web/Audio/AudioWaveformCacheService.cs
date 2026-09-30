using StoryForge.Core.Audio;

namespace StoryForge.Web.Audio;

// Singleton, not scoped: the cache file is one per-machine file, and two browser tabs (two circuits) each
// writing their own in-memory copy of it would clobber each other. Saves are debounced because a first
// open computes a waveform per file in quick succession — one write after the burst, not one per file.
public sealed class AudioWaveformCacheService : IDisposable
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(1);

    private readonly object _lock = new();
    private readonly AudioWaveformCache _cache = AudioWaveformCache.LoadOrDefault();
    private readonly Timer _saveTimer;

    public AudioWaveformCacheService()
    {
        _saveTimer = new Timer(_ => SaveNow(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public AudioWaveformCacheEntry? TryGet(AudioCategory category, AudioEntry entry)
    {
        lock (_lock)
        {
            return _cache.Entries.TryGetValue(AudioWaveformCache.GetKey(category, entry.FileName), out var cached) && cached.Matches(entry)
                ? cached
                : null;
        }
    }

    public AudioWaveformCacheEntry Put(AudioCategory category, AudioEntry entry, double durationSeconds, byte[] peaks)
    {
        var cached = new AudioWaveformCacheEntry
        {
            SizeBytes = entry.SizeBytes,
            LastWriteUtcTicks = entry.LastWriteUtc.Ticks,
            DurationSeconds = durationSeconds,
            Peaks = peaks,
        };

        lock (_lock)
        {
            _cache.Entries[AudioWaveformCache.GetKey(category, entry.FileName)] = cached;
        }

        _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
        return cached;
    }

    public void Remove(AudioCategory category, string fileName)
    {
        lock (_lock)
        {
            _cache.Entries.Remove(AudioWaveformCache.GetKey(category, fileName));
        }

        _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
    }

    private void SaveNow()
    {
        try
        {
            lock (_lock)
            {
                _cache.Save();
            }
        }
        catch
        {
            // Derived data — a failed save just means recomputing next time.
        }
    }

    public void Dispose()
    {
        _saveTimer.Dispose();
        SaveNow();
    }
}
