using System.Security.Principal;

namespace Athena.Desktop.Hosting;

public sealed class DesktopInstance : IDisposable
{
    private static readonly NamedWaitHandleOptions Scope = new() { CurrentUserOnly = true, CurrentSessionOnly = true };
    private readonly Mutex mutex;
    private readonly EventWaitHandle stop;
    private DesktopInstance(Mutex mutex, string name)
    {
        this.mutex = mutex;
        stop = new EventWaitHandle(false, EventResetMode.ManualReset, name + ".Stop", Scope);
        stop.Reset();
    }

    private static string DefaultName => "Athena.Desktop." + WindowsIdentity.GetCurrent().User!.Value;

    // Mutex ownership belongs to the calling thread; dispose on that same thread.
    public static DesktopInstance? TryAcquire(string? name = null)
    {
        name ??= DefaultName;
        var mutex = new Mutex(false, name, Scope);
        if (!Acquire(mutex, TimeSpan.Zero))
        {
            mutex.Dispose();
            return null;
        }
        try { return new DesktopInstance(mutex, name); }
        catch { mutex.ReleaseMutex(); mutex.Dispose(); throw; }
    }

    public static bool IsRunning(string? name = null)
    {
        using var mutex = new Mutex(false, name ?? DefaultName, Scope);
        if (!Acquire(mutex, TimeSpan.Zero)) return true;
        mutex.ReleaseMutex();
        return false;
    }

    public static bool Stop(TimeSpan timeout, string? name = null)
    {
        name ??= DefaultName;
        if (!EventWaitHandle.TryOpenExisting(name + ".Stop", Scope, out var stop))
            return !IsRunning(name);
        using (stop)
        using (var mutex = new Mutex(false, name, Scope))
        {
            stop.Set();
            if (!Acquire(mutex, timeout)) return false;
            mutex.ReleaseMutex();
            return true;
        }
    }

    public IDisposable OnStop(Action callback) => new StopRegistration(
        ThreadPool.RegisterWaitForSingleObject(stop, (_, _) => callback(), null, Timeout.Infinite, true));

    private static bool Acquire(Mutex mutex, TimeSpan timeout)
    {
        try { return mutex.WaitOne(timeout); }
        catch (AbandonedMutexException) { return true; }
    }

    public void Dispose()
    {
        stop.Dispose();
        mutex.ReleaseMutex();
        mutex.Dispose();
    }

    private sealed class StopRegistration(RegisteredWaitHandle registration) : IDisposable
    {
        public void Dispose() => registration.Unregister(null);
    }
}
