using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MD301Plus
{
    internal sealed class LaunchJob : IDisposable
    {
        private readonly SafeFileHandle handle;

        internal LaunchJob()
        {
            handle = CreateJobObject(IntPtr.Zero, null);
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot create the launch lifetime boundary.");
            try
            {
                ExtendedLimits limits = new ExtendedLimits();
                // Kill the helper on parent exit, but never keep apps it launches in this job.
                limits.Basic.Flags = 0x2000 | 0x1000; // KILL_ON_JOB_CLOSE | SILENT_BREAKAWAY_OK
                if (!SetInformationJobObject(handle, 9, ref limits, Marshal.SizeOf(typeof(ExtendedLimits))))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot configure the launch lifetime boundary.");
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        internal void Attach(Process helper)
        {
            if (!AssignProcessToJobObject(handle, helper.Handle))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot bind the launch helper to MD301 Plus.");
        }

        public void Dispose() { handle.Dispose(); }

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimits
        {
            internal long ProcessTime, JobTime;
            internal uint Flags;
            internal UIntPtr MinimumWorkingSet, MaximumWorkingSet;
            internal uint ActiveProcesses;
            internal UIntPtr Affinity;
            internal uint Priority, Scheduling;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            internal ulong ReadOperations, WriteOperations, OtherOperations;
            internal ulong ReadBytes, WriteBytes, OtherBytes;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimits
        {
            internal BasicLimits Basic;
            internal IoCounters Io;
            internal UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass,
            ref ExtendedLimits limits, int length);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    }
}
