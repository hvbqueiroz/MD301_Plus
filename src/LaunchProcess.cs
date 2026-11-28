using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace MD301Plus
{
    // Shell/COM calls and network lookups cannot be safely canceled inside the tray process.
    internal sealed class LaunchProcess : IDisposable
    {
        internal const string HelperArgument = "--launch-helper";
        private readonly object gate = new object();
        private readonly int timeoutMs;
        private readonly string argument;
        private Process active;
        private bool disposed;

        internal LaunchProcess(int timeoutMs = 10000, string argument = HelperArgument)
        {
            if (timeoutMs <= 0) throw new ArgumentOutOfRangeException("timeoutMs");
            this.timeoutMs = timeoutMs;
            this.argument = argument;
        }

        internal void Open(string target)
        {
            using (ManualResetEvent responseReady = new ManualResetEvent(false))
            {
                string response = null;
                bool responseClosed = false;
                object responseGate = new object();
                Process helper = new Process();
                helper.StartInfo = new ProcessStartInfo(typeof(Program).Assembly.Location,
                    argument + " " + Convert.ToBase64String(Encoding.UTF8.GetBytes(target)))
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true
                };
                DataReceivedEventHandler receive = delegate(object sender, DataReceivedEventArgs data)
                {
                    if (data.Data == null) return;
                    lock (responseGate)
                    {
                        if (!responseClosed && response == null)
                        {
                            response = data.Data;
                            responseReady.Set();
                        }
                    }
                };
                helper.OutputDataReceived += receive;
                bool started = false;
                try
                {
                    lock (gate)
                    {
                        if (disposed) throw new ObjectDisposedException("LaunchProcess");
                        if (active != null)
                        {
                            if (!active.HasExited)
                                throw new InvalidOperationException("The previous launch helper could not be stopped. Restart MD301 Plus before launching another app.");
                            active.Dispose();
                            active = null;
                        }
                        helper.Start();
                        started = true;
                        active = helper;
                        Native.AllowSetForegroundWindow((uint)helper.Id);
                    }
                    Stopwatch clock = Stopwatch.StartNew();
                    helper.BeginOutputReadLine();
                    if (!helper.WaitForExit(timeoutMs))
                    {
                        Stop();
                        throw new TimeoutException("Opening the app took too long. The launch helper was stopped. " +
                            "Windows may already have accepted the request; check whether the app opened before trying again.");
                    }
                    lock (gate)
                    {
                        if (disposed) throw new OperationCanceledException("The launch helper was stopped during shutdown.");
                    }
                    int remaining = (int)Math.Max(0, timeoutMs - clock.ElapsedMilliseconds);
                    responseReady.WaitOne(remaining);
                    string result;
                    lock (responseGate) result = response;
                    if (result == null)
                        throw new InvalidOperationException("The launch helper exited without reporting a result (exit code " + helper.ExitCode + ").");
                    ReadResult(result, target, helper.ExitCode);
                }
                catch (Exception failure)
                {
                    if (started)
                    {
                        try { Stop(); }
                        catch (Exception stopError)
                        {
                            throw new InvalidOperationException("The launch helper could not be stopped. Restart MD301 Plus before launching another app.",
                                new AggregateException(failure, stopError));
                        }
                    }
                    throw;
                }
                finally
                {
                    lock (responseGate)
                    {
                        responseClosed = true;
                        helper.OutputDataReceived -= receive;
                    }
                    lock (gate)
                    {
                        if (!started || helper.HasExited)
                        {
                            if (active == helper) active = null;
                            helper.Dispose();
                        }
                    }
                }
            }
        }

        private void Stop()
        {
            lock (gate)
            {
                if (active == null || active.HasExited) return;
                active.Kill();
                if (!active.WaitForExit(1000))
                    throw new TimeoutException("The launch helper did not stop. Restart MD301 Plus before launching another app.");
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
            }
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { Stop(); }
                catch (Exception error) { AppLog.Error("Stopping the launch helper during shutdown", error); }
            });
        }

        internal static int RunHelper(string encodedTarget)
        {
            try
            {
                if (encodedTarget == null || encodedTarget.Length > 8192)
                    throw new SettingsException("The launch helper received an invalid target.");
                string target = Encoding.UTF8.GetString(Convert.FromBase64String(encodedTarget));
                RingAction.ValidateLaunchTarget(target);
                if (!RingAction.IsUriLaunchTarget(target) && !File.Exists(target) && !Directory.Exists(target))
                    throw new FileNotFoundException("The launch target does not exist: " + target, target);
                ShellLauncher.Open(target);
                Console.WriteLine("OK");
                return 0;
            }
            catch (Exception error)
            {
                Win32Exception native = error as Win32Exception;
                string kind = native != null ? "WIN32:" + native.NativeErrorCode :
                    error is FileNotFoundException ? "FILE" : error is SettingsException ? "SETTINGS" : "ERROR";
                Console.WriteLine(kind + "|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(error.Message)));
                return 1;
            }
        }

        private static void ReadResult(string result, string target, int exitCode)
        {
            if (result == "OK" && exitCode == 0) return;
            int separator = result.IndexOf('|');
            if (separator <= 0) throw new InvalidOperationException("The launch helper returned an invalid result.");
            string kind = result.Substring(0, separator);
            string message = Encoding.UTF8.GetString(Convert.FromBase64String(result.Substring(separator + 1)));
            if (kind == "FILE") throw new FileNotFoundException(message, target);
            if (kind == "SETTINGS") throw new SettingsException(message);
            if (kind.StartsWith("WIN32:", StringComparison.Ordinal))
                throw new Win32Exception(int.Parse(kind.Substring(6)), message);
            throw new InvalidOperationException(message);
        }
    }
}
