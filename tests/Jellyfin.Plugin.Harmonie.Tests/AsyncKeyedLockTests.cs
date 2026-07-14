using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Harmonie.Services;
using Xunit;

namespace Jellyfin.Plugin.Harmonie.Tests;

public class AsyncKeyedLockTests
{
    [Fact]
    public async Task Same_key_waits_until_the_current_owner_releases()
    {
        var keyedLock = new AsyncKeyedLock<Guid>();
        var key = Guid.NewGuid();
        using var first = await keyedLock.AcquireAsync(key, CancellationToken.None);

        var secondTask = keyedLock.AcquireAsync(key, CancellationToken.None);
        Assert.False(secondTask.IsCompleted);

        first.Dispose();
        using var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Different_keys_can_be_owned_concurrently()
    {
        var keyedLock = new AsyncKeyedLock<Guid>();
        using var first = await keyedLock.AcquireAsync(Guid.NewGuid(), CancellationToken.None);

        var secondTask = keyedLock.AcquireAsync(Guid.NewGuid(), CancellationToken.None);
        using var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Waiting_for_a_busy_key_honors_cancellation()
    {
        var keyedLock = new AsyncKeyedLock<Guid>();
        var key = Guid.NewGuid();
        using var first = await keyedLock.AcquireAsync(key, CancellationToken.None);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => keyedLock.AcquireAsync(key, cts.Token));
    }
}
