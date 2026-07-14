using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Harmonie.Services;

/// <summary>
/// Serializes asynchronous operations that share the same key while allowing
/// operations for different keys to proceed concurrently.
/// </summary>
/// <typeparam name="TKey">Key used to select a lock.</typeparam>
public sealed class AsyncKeyedLock<TKey>
    where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, SemaphoreSlim> _locks = new();

    /// <summary>
    /// Waits for exclusive ownership of <paramref name="key"/>.
    /// Dispose the returned lease to release it.
    /// </summary>
    public async Task<IDisposable> AcquireAsync(TKey key, CancellationToken cancellationToken)
    {
        // Entries intentionally live for the lifetime of this object. Removing
        // an idle semaphore races with a concurrent waiter obtaining the same
        // instance and can allow two owners for one key. Playlist counts are
        // small, so retaining one semaphore per observed id is the safer bound.
        var semaphore = _locks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(semaphore);
    }

    private sealed class Releaser : IDisposable
    {
        private SemaphoreSlim? _semaphore;

        public Releaser(SemaphoreSlim semaphore)
        {
            _semaphore = semaphore;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _semaphore, null)?.Release();
        }
    }
}
