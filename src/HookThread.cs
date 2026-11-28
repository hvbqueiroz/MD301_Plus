using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MD301Plus
{
    // Low-level hooks must keep pumping messages even when the editor or a shell operation is busy.
    internal sealed class HookThread : IDisposable
    {
        private readonly Thread thread;
        private readonly SynchronizationContext owner;
        private volatile Control dispatcher;
        private volatile bool disposed;
        private int stopped;
        private readonly TaskCompletionSource<bool> stoppedCompletion = new TaskCompletionSource<bool>();
        private bool stopCounted;
        private static readonly object stopLock = new object();
        private static int stopping;
        internal const int StartupTimeoutMs = 2000;

        internal HookThread(Action install, Action release, int startupTimeoutMs = StartupTimeoutMs)
        {
            if (startupTimeoutMs <= 0) throw new ArgumentOutOfRangeException("startupTimeoutMs");
            owner = new WindowsFormsSynchronizationContext();
            Stopwatch startup = Stopwatch.StartNew();
            while (HasStoppingThreads() && startup.ElapsedMilliseconds < startupTimeoutMs) Thread.Sleep(1);
            if (HasStoppingThreads())
                throw new Win32Exception(1460, "The previous input listener is still stopping. Try again after it stops, or restart MD301 Plus.");
            Exception startupError = null;
            int ready = 0;
            thread = new Thread(delegate()
            {
                bool started = false;
                try
                {
                    using (Control window = new Control())
                    {
                        dispatcher = window;
                        window.CreateControl();
                        if (window.Handle == IntPtr.Zero)
                            throw new InvalidOperationException("The input message window could not be created.");
                        install();
                        if (disposed) return;
                        window.BeginInvoke(new Action(delegate
                        {
                            started = true;
                            Interlocked.Exchange(ref ready, 1);
                        }));
                        Application.Run();
                        if (!disposed && started)
                            ReportFailure(new InvalidOperationException("The input message loop stopped unexpectedly."));
                    }
                }
                catch (Exception error)
                {
                    if (!started) startupError = error;
                    else ReportFailure(error);
                }
                finally
                {
                    try { release(); }
                    catch (Exception error) { ReportFailure(error); }
                    finally
                    {
                        lock (stopLock)
                        {
                            Interlocked.Exchange(ref stopped, 1);
                            if (stopCounted) { stopping--; stopCounted = false; }
                        }
                        if (!started)
                        {
                            if (startupError == null)
                                startupError = new InvalidOperationException("The input message loop stopped before it became ready.");
                            Interlocked.Exchange(ref ready, 1);
                        }
                        stoppedCompletion.TrySetResult(true);
                    }
                }
            });
            thread.Name = "MD301 Plus input";
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            // STA waits such as Join/WaitOne can pump UI messages and reenter ring creation or disposal.
            while (Thread.VolatileRead(ref ready) == 0 && startup.ElapsedMilliseconds < startupTimeoutMs) Thread.Sleep(1);
            if (Thread.VolatileRead(ref ready) == 0)
            {
                Dispose();
                throw new Win32Exception(1460, "The input listener did not become ready in time. Try again after it stops, or restart MD301 Plus.");
            }
            if (startupError != null) ExceptionDispatchInfo.Capture(startupError).Throw();
        }

        internal bool IsRunning { get { return !disposed && Thread.VolatileRead(ref stopped) == 0 && thread.IsAlive; } }
        internal bool IsStopped { get { return Thread.VolatileRead(ref stopped) != 0; } }

        private static bool HasStoppingThreads()
        {
            lock (stopLock) return stopping != 0;
        }

        internal void WhenStopped(Action<bool> callback)
        {
            CancellationTokenSource deadline = new CancellationTokenSource();
            Task.WhenAny(stoppedCompletion.Task, Task.Delay(StartupTimeoutMs, deadline.Token)).ContinueWith(delegate
            {
                deadline.Cancel();
                deadline.Dispose();
                bool finished = stoppedCompletion.Task.IsCompleted;
                try { owner.Post(delegate { callback(finished); }, null); }
                catch (InvalidOperationException error) { AppLog.Error("Completing input listener cleanup", error); }
            }, TaskScheduler.Default);
        }

        private void ReportFailure(Exception error)
        {
            AppLog.Error("Input listener stopped", error);
            try
            {
                if (!disposed) owner.Post(delegate
                {
                    if (!disposed) AppLog.ShowError(null, "Input listener stopped",
                        new InvalidOperationException("Save your button assignment again, or restart MD301 Plus.", error));
                }, null);
            }
            catch (InvalidOperationException dispatchError)
            {
                if (!disposed) AppLog.Error("Reporting an input listener failure", dispatchError);
            }
        }

        public void Dispose()
        {
            lock (stopLock)
            {
                if (disposed) return;
                disposed = true;
                if (!IsStopped) { stopping++; stopCounted = true; }
            }
            Control window = dispatcher;
            if (thread.IsAlive && window != null)
            {
                try { window.BeginInvoke(new Action(Application.ExitThread)); }
                catch (InvalidOperationException error)
                {
                    if (!IsStopped) ThreadPool.QueueUserWorkItem(delegate { AppLog.Error("Stopping the input listener", error); });
                }
            }
        }
    }
}
