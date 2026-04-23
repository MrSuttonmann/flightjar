using FlightJar.Clients.Caching;
using Microsoft.Extensions.Time.Testing;

namespace FlightJar.Clients.Tests.Caching;

public class HttpThrottleTests
{
    [Fact]
    public async Task CancellingInterRequestDelay_ReleasesGate_SoNextAcquireSucceeds()
    {
        // Regression: a cancellation during the min-interval Task.Delay used
        // to leak the semaphore permit, wedging the throttle forever. First
        // caller returns the Releaser, second caller cancels mid-delay, a
        // third caller must still be able to acquire.
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var throttle = new HttpThrottle(TimeSpan.FromSeconds(10));

        // First acquire: succeeds immediately, gate held by Releaser.
        var first = await throttle.AcquireAsync(time, CancellationToken.None);
        await first.DisposeAsync(); // permit returned, _lastRequestAt bumped.

        // Second acquire: will have to wait 10 s for the min interval. We
        // cancel after kicking it off — the buggy version leaks the permit
        // here and the next caller blocks forever.
        using var cts = new CancellationTokenSource();
        var secondTask = throttle.AcquireAsync(time, cts.Token);
        cts.Cancel();
        // FakeTimeProvider's Task.Delay is token-aware, so cancellation
        // short-circuits the wait without advancing virtual time.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await secondTask);

        // Third acquire: with the gate correctly released, this should not
        // block indefinitely. A short CancellationToken gives the test a
        // real failure mode (timeout) rather than hanging forever.
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        time.Advance(TimeSpan.FromSeconds(20));
        var third = await throttle.AcquireAsync(time, watchdog.Token);
        Assert.NotNull(third);
        await third.DisposeAsync();
    }

    [Fact]
    public async Task CancellingWhileWaitingForGate_DoesNotStealPermit()
    {
        // If WaitAsync itself is cancelled (gate held by someone else),
        // the permit was never taken — nothing to release. Making sure the
        // new catch path doesn't double-release and thus let a second
        // waiter in while the first is still using its permit.
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var throttle = new HttpThrottle(TimeSpan.FromMilliseconds(1));

        var holder = await throttle.AcquireAsync(time, CancellationToken.None);
        // Gate is now held by `holder`.

        using var cts = new CancellationTokenSource();
        var waiterTask = throttle.AcquireAsync(time, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waiterTask);

        // Gate must still be held exclusively by `holder`. If the cancel
        // path had released the permit a second acquirer could now barge
        // in — verify it can't by racing another with a tight watchdog
        // and expecting it to time out (not complete).
        using var watchdog = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await throttle.AcquireAsync(time, watchdog.Token));

        await holder.DisposeAsync();
    }
}
