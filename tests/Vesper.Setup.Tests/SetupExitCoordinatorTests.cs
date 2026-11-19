namespace Vesper.Setup.Tests;

public sealed class SetupExitCoordinatorTests
{
    [Fact]
    public void AcceptedHandoffWaitsForMutexRelease()
    {
        string mutexName = MutexName();
        using var mutex = new Mutex(false, mutexName);
        using var owned = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Thread owner = StartOwner(mutex, owned, release);
        Assert.True(owned.Wait(TimeSpan.FromSeconds(2)));

        try
        {
            using IDisposable appLock = Program.AcquireAppLock(mutexName, _ =>
            {
                release.Set();
                return Task.FromResult(true);
            }, TimeSpan.FromSeconds(2));
            Assert.True(owner.Join(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            release.Set();
            owner.Join(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public void DeclinedHandoffLeavesTheAppMutexOwned()
    {
        string mutexName = MutexName();
        using var mutex = new Mutex(false, mutexName);
        using var owned = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Thread owner = StartOwner(mutex, owned, release);
        Assert.True(owned.Wait(TimeSpan.FromSeconds(2)));

        try
        {
            IOException error = Assert.Throws<IOException>(() => Program.AcquireAppLock(mutexName,
                _ => Task.FromResult(false), TimeSpan.FromSeconds(1)));
            Assert.Contains("Accept its exit prompt", error.Message);
            AssertMutexStillOwned(mutexName);
        }
        finally
        {
            release.Set();
            owner.Join(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public void UnavailableOlderAppTimesOutAndLeavesTheAppMutexOwned()
    {
        string mutexName = MutexName();
        using var mutex = new Mutex(false, mutexName);
        using var owned = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Thread owner = StartOwner(mutex, owned, release);
        Assert.True(owned.Wait(TimeSpan.FromSeconds(2)));

        try
        {
            IOException error = Assert.Throws<IOException>(() => Program.AcquireAppLock(mutexName,
                _ => Task.FromException<bool>(new TimeoutException("no setup pipe")), TimeSpan.FromSeconds(1)));
            Assert.Contains("Close VESPER", error.Message);
            AssertMutexStillOwned(mutexName);
        }
        finally
        {
            release.Set();
            owner.Join(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public void LostReplySucceedsIfTheMutexWasReleased()
    {
        string mutexName = MutexName();
        using var mutex = new Mutex(false, mutexName);
        using var owned = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var released = new ManualResetEventSlim();
        Thread owner = new(() =>
        {
            mutex.WaitOne();
            owned.Set();
            release.Wait();
            mutex.ReleaseMutex();
            released.Set();
        }) { IsBackground = true };
        owner.Start();
        Assert.True(owned.Wait(TimeSpan.FromSeconds(2)));

        try
        {
            using IDisposable appLock = Program.AcquireAppLock(mutexName, _ =>
            {
                release.Set();
                Assert.True(released.Wait(TimeSpan.FromSeconds(2)));
                return Task.FromException<bool>(new TimeoutException("lost reply"));
            }, TimeSpan.FromSeconds(1));
            Assert.True(owner.Join(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            release.Set();
            owner.Join(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public void AbandonedMutexIsAcquiredAfterItsOwnerThreadExits()
    {
        string mutexName = MutexName();
        using var mutex = new Mutex(false, mutexName);
        using var owned = new ManualResetEventSlim();
        Thread owner = new(() =>
        {
            mutex.WaitOne();
            owned.Set();
        }) { IsBackground = true };
        owner.Start();
        Assert.True(owned.Wait(TimeSpan.FromSeconds(2)));
        Assert.True(owner.Join(TimeSpan.FromSeconds(2)));

        using IDisposable appLock = Program.AcquireAppLock(mutexName,
            _ => throw new InvalidOperationException("An abandoned mutex means the app owner already exited."),
            TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void CancellationStopsTheExitRequest()
    {
        string mutexName = MutexName();
        using var mutex = new Mutex(false, mutexName);
        using var owned = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Thread owner = StartOwner(mutex, owned, release);
        Assert.True(owned.Wait(TimeSpan.FromSeconds(2)));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        try
        {
            Assert.ThrowsAny<OperationCanceledException>(() => Program.AcquireAppLock(mutexName,
                async ct =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    return true;
                }, TimeSpan.FromSeconds(1), cancellation.Token));
            AssertMutexStillOwned(mutexName);
        }
        finally
        {
            release.Set();
            owner.Join(TimeSpan.FromSeconds(2));
        }
    }

    private static Thread StartOwner(Mutex mutex, ManualResetEventSlim owned, ManualResetEventSlim release)
    {
        var owner = new Thread(() =>
        {
            mutex.WaitOne();
            owned.Set();
            release.Wait();
            mutex.ReleaseMutex();
        }) { IsBackground = true };
        owner.Start();
        return owner;
    }

    private static void AssertMutexStillOwned(string mutexName)
    {
        using var probe = new Mutex(false, mutexName);
        Assert.False(probe.WaitOne(0));
    }

    private static string MutexName() => $@"Local\VESPER.Setup.Tests-{Guid.NewGuid():N}";
}
