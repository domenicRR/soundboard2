using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace CS2Soundboard.Core.Audio;

/// <summary>
/// A dedicated MTA thread on which all device operations (enumeration, start, stop, dispose) run.
/// Keeps COM objects on one apartment and keeps slow device calls off the UI thread.
/// </summary>
public sealed class AudioWorker : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;

    public AudioWorker()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "Audio control" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public bool IsCurrentThread => Thread.CurrentThread == _thread;

    private void Loop()
    {
        foreach (var action in _queue.GetConsumingEnumerable())
        {
            try { action(); }
            catch { /* actions report their own errors via their Task */ }
        }
    }

    public Task RunAsync(Action action) => RunAsync(() => { action(); return true; });

    public Task<T> RunAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (IsCurrentThread)
        {
            try { tcs.SetResult(func()); } catch (Exception ex) { tcs.SetException(ex); }
            return tcs.Task;
        }
        try
        {
            _queue.Add(() =>
            {
                try { tcs.SetResult(func()); }
                catch (Exception ex) { tcs.SetException(ex); }
            });
        }
        catch (InvalidOperationException)
        {
            tcs.SetException(new ObjectDisposedException(nameof(AudioWorker)));
        }
        return tcs.Task;
    }

    /// <summary>Runs <paramref name="action"/> synchronously on the worker (used at shutdown).</summary>
    public void Invoke(Action action, TimeSpan timeout)
    {
        var task = RunAsync(action);
        task.Wait(timeout);
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        if (!IsCurrentThread) _thread.Join(TimeSpan.FromSeconds(3));
    }
}
