using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32.SafeHandles;

namespace MD301Plus
{
    internal enum BatteryRead { Ok, Asleep, Missing, Failed, Pending }

    internal delegate BatteryRead BatterySource(out int percent);
    internal delegate BatteryRead BatteryQuery(string path, out int percent);

    internal sealed class BatteryReading
    {
        private readonly Func<DateTime> utcNow;
        internal int Percent { get; private set; }
        internal BatteryRead State { get; private set; }
        internal DateTime? UpdatedUtc { get; private set; }

        internal BatteryReading(int percent, BatteryRead state, DateTime? updatedUtc, Func<DateTime> utcNow)
        {
            Percent = percent;
            State = state;
            UpdatedUtc = updatedUtc;
            this.utcNow = utcNow;
        }

        internal bool IsCurrent
        {
            get
            {
                if (State != BatteryRead.Ok || !UpdatedUtc.HasValue) return false;
                TimeSpan age = utcNow() - UpdatedUtc.Value;
                return age >= TimeSpan.Zero && age < TimeSpan.FromMinutes(10);
            }
        }

        internal string Readout
        {
            get { return Percent < 0 ? "--" : Percent + "%" + (IsCurrent ? "" : "*"); }
        }

        internal string Description
        {
            get
            {
                if (State == BatteryRead.Pending) return "Checking the MD301 battery level.";
                string text = Percent < 0 ? "Mouse battery level unavailable." :
                    "Mouse battery " + Percent + "%" + (IsCurrent ? "." : " (last known).");
                if (UpdatedUtc.HasValue) text += " Last updated " + UpdatedUtc.Value.ToLocalTime().ToString("g") + ".";
                if (State == BatteryRead.Missing) text += " No compatible MD301 USB connection was found.";
                else if (State == BatteryRead.Asleep) text += " The mouse is asleep or not responding.";
                else if (State == BatteryRead.Failed) text += " The last battery read failed.";
                else if (!IsCurrent) text += " The battery reading is out of date.";
                return text;
            }
        }
    }

    // Reads the battery level through the ASUS vendor HID collection that Armoury Crate and Gear Link use.
    // Only the read-only "get battery" query (0x0712) is sent, and only to collections that name the MD301.
    internal static class MouseBatteryReader
    {
        internal const int ReportLength = 65;
        private const ushort AsusVendorId = 0x0B05;
        private const ushort VendorUsagePage = 0xFF01;
        private const int TimeoutMs = 1000;
        private static string cachedPath;

        internal static byte[] BuildRequest()
        {
            byte[] request = new byte[ReportLength];
            request[1] = 0x12;
            request[2] = 0x07;
            return request;
        }

        internal static bool IsBatteryEcho(byte[] report)
        {
            return report != null && report.Length >= 7 && report[1] == 0x12 && report[2] == 0x07;
        }

        // ASUS answers 0xAAFF when the receiver cannot reach a sleeping or switched-off mouse.
        internal static bool IsUnavailable(byte[] report)
        {
            return report != null && report.Length >= 3 && report[1] == 0xFF && report[2] == 0xAA;
        }

        internal static int ParseResponse(byte[] report)
        {
            if (!IsBatteryEcho(report)) return MouseBattery.Unknown;
            int percent = report[5];
            if (percent > 100) return MouseBattery.Unknown;
            for (int i = 5; i < report.Length; i++)
                if (report[i] != 0) return percent;
            return MouseBattery.Unknown;
        }

        internal static BatteryRead Read(out int percent)
        {
            return ReadCollections(out percent, ref cachedPath, FindCollections, Query);
        }

        internal static BatteryRead ReadCollections(out int percent, ref string cached,
            Func<List<string>> collections, BatteryQuery query)
        {
            percent = MouseBattery.Unknown;
            string path = cached;
            Exception failure = null;
            if (path != null)
            {
                try
                {
                    BatteryRead cachedResult = query(path, out percent);
                    if (cachedResult != BatteryRead.Missing) return cachedResult;
                }
                catch (Win32Exception error) { failure = error; }
                catch (IOException error) { failure = error; }
                cached = null;
            }
            BatteryRead result = BatteryRead.Missing;
            foreach (string candidate in collections())
            {
                if (candidate == path) continue;
                int value;
                BatteryRead read;
                try { read = query(candidate, out value); }
                catch (Win32Exception error) { if (failure == null) failure = error; continue; }
                catch (IOException error) { if (failure == null) failure = error; continue; }
                if (read == BatteryRead.Missing) continue;
                cached = candidate;
                result = read;
                percent = value;
                if (read == BatteryRead.Ok) return read;
            }
            if (failure != null)
            {
                cached = null;
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            }
            return result;
        }

        internal static List<string> FindCollections()
        {
            List<string> result = new List<string>();
            Guid hidClass;
            HidD_GetHidGuid(out hidClass);
            IntPtr set = SetupDiGetClassDevs(ref hidClass, IntPtr.Zero, IntPtr.Zero, 0x12);
            if (set == new IntPtr(-1))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot enumerate HID devices.");
            try
            {
                for (int index = 0; ; index++)
                {
                    DeviceInterfaceData item = new DeviceInterfaceData(Marshal.SizeOf(typeof(DeviceInterfaceData)));
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidClass, index, ref item))
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error == 259) break; // ERROR_NO_MORE_ITEMS
                        throw new Win32Exception(error, "Cannot enumerate a HID device interface.");
                    }
                    string path = InterfacePath(set, ref item);
                    if (path != null && path.IndexOf("0b05", StringComparison.OrdinalIgnoreCase) >= 0 && IsMd301Protocol(path))
                        result.Add(path);
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return result;
        }

        private static string InterfacePath(IntPtr set, ref DeviceInterfaceData item)
        {
            int required;
            SetupDiGetDeviceInterfaceDetail(set, ref item, IntPtr.Zero, 0, out required, IntPtr.Zero);
            int error = Marshal.GetLastWin32Error();
            if (error != 122)
            {
                if (ClassifyFailure(error, false) != BatteryRead.Missing)
                    throw new Win32Exception(error, "Cannot read a HID device interface.");
                return null; // A device disconnected during enumeration.
            }
            if (required <= 4) throw new IOException("Windows returned an invalid HID device interface size.");
            IntPtr detail = Marshal.AllocHGlobal(required);
            try
            {
                // SP_DEVICE_INTERFACE_DETAIL_DATA_W.cbSize: 8 on 64-bit, 6 on 32-bit.
                Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                if (!SetupDiGetDeviceInterfaceDetail(set, ref item, detail, required, out required, IntPtr.Zero))
                {
                    error = Marshal.GetLastWin32Error();
                    if (ClassifyFailure(error, false) != BatteryRead.Missing)
                        throw new Win32Exception(error, "Cannot read a HID device interface.");
                    return null;
                }
                return Marshal.PtrToStringUni(new IntPtr(detail.ToInt64() + 4));
            }
            finally { Marshal.FreeHGlobal(detail); }
        }

        private static bool IsMd301Protocol(string path)
        {
            using (SafeFileHandle handle = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
            {
                if (handle.IsInvalid) return false;
                HidAttributes attributes = new HidAttributes(Marshal.SizeOf(typeof(HidAttributes)));
                if (!HidD_GetAttributes(handle, ref attributes) || attributes.VendorId != AsusVendorId) return false;
                IntPtr parsed;
                if (!HidD_GetPreparsedData(handle, out parsed)) return false;
                HidCaps caps = new HidCaps();
                try
                {
                    if (HidP_GetCaps(parsed, ref caps) != 0x00110000) return false;
                }
                finally { HidD_FreePreparsedData(parsed); }
                if (caps.UsagePage != VendorUsagePage || caps.Usage != 1 ||
                    caps.InputReportByteLength != ReportLength || caps.OutputReportByteLength != ReportLength) return false;
                byte[] name = new byte[256];
                if (!HidD_GetProductString(handle, name, name.Length)) return false;
                string product = System.Text.Encoding.Unicode.GetString(name).Split('\0')[0];
                return product.IndexOf("MD301", StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }

        private static BatteryRead Query(string path, out int percent)
        {
            percent = MouseBattery.Unknown;
            using (SafeFileHandle handle = CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
            {
                if (handle.IsInvalid) return ClassifyFailure(Marshal.GetLastWin32Error(), false);
                // The vendor collection only answers on a synchronous handle, so a watchdog cancels blocked I/O.
                using (System.Threading.Timer watchdog = new System.Threading.Timer(delegate
                {
                    try { CancelIoEx(handle, IntPtr.Zero); }
                    catch (ObjectDisposedException) { }
                }, null, Timeout.Infinite, Timeout.Infinite))
                {
                    Stopwatch clock = Stopwatch.StartNew();
                    byte[] request = BuildRequest();
                    BatteryRead transfer = Transfer(true, handle, request, watchdog, clock);
                    if (transfer != BatteryRead.Ok) return transfer;
                    for (int attempt = 0; attempt < 8; attempt++)
                    {
                        byte[] report = new byte[ReportLength];
                        transfer = Transfer(false, handle, report, watchdog, clock);
                        if (transfer != BatteryRead.Ok) return transfer;
                        if (IsUnavailable(report)) return BatteryRead.Asleep;
                        if (!IsBatteryEcho(report)) continue;
                        percent = ParseResponse(report);
                        return percent == MouseBattery.Unknown ? BatteryRead.Asleep : BatteryRead.Ok;
                    }
                    return BatteryRead.Asleep;
                }
            }
        }

        internal static BatteryRead ClassifyFailure(int error, bool timedOut)
        {
            if (error == 2 || error == 3 || error == 433 || error == 1167) return BatteryRead.Missing;
            if (error == 121 || error == 1460 || (error == 995 && timedOut)) return BatteryRead.Asleep;
            throw new Win32Exception(error, "The MD301 battery query failed (Windows error " + error + ").");
        }

        private static BatteryRead Transfer(bool write, SafeFileHandle handle, byte[] buffer,
            System.Threading.Timer watchdog, Stopwatch clock)
        {
            long remaining = TimeoutMs - clock.ElapsedMilliseconds;
            if (remaining <= 0) return BatteryRead.Asleep;
            watchdog.Change(remaining, Timeout.Infinite);
            try
            {
                int count;
                bool ok = write ? WriteFile(handle, buffer, buffer.Length, out count, IntPtr.Zero) :
                    ReadFile(handle, buffer, buffer.Length, out count, IntPtr.Zero);
                if (!ok) return ClassifyFailure(Marshal.GetLastWin32Error(), clock.ElapsedMilliseconds >= TimeoutMs);
                if (count != buffer.Length) throw new IOException("The MD301 battery query returned an incomplete HID report.");
                return BatteryRead.Ok;
            }
            finally { watchdog.Change(Timeout.Infinite, Timeout.Infinite); }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DeviceInterfaceData
        {
            public int Size;
            public Guid InterfaceClass;
            public int Flags;
            public IntPtr Reserved;
            public DeviceInterfaceData(int size) { Size = size; InterfaceClass = Guid.Empty; Flags = 0; Reserved = IntPtr.Zero; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HidAttributes
        {
            public int Size;
            public ushort VendorId;
            public ushort ProductId;
            public ushort Version;
            public HidAttributes(int size) { Size = size; VendorId = 0; ProductId = 0; Version = 0; }
        }

        [StructLayout(LayoutKind.Sequential, Size = 64)]
        private struct HidCaps
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
        }

        [DllImport("hid.dll")]
        private static extern void HidD_GetHidGuid(out Guid hidClass);

        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool HidD_GetAttributes(SafeFileHandle device, ref HidAttributes attributes);

        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool HidD_GetPreparsedData(SafeFileHandle device, out IntPtr parsed);

        [DllImport("hid.dll")]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool HidD_FreePreparsedData(IntPtr parsed);

        [DllImport("hid.dll")]
        private static extern int HidP_GetCaps(IntPtr parsed, ref HidCaps caps);

        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool HidD_GetProductString(SafeFileHandle device, byte[] buffer, int length);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr parent, int flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr device, ref Guid classGuid, int index,
            ref DeviceInterfaceData item);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref DeviceInterfaceData item,
            IntPtr detail, int size, out int required, IntPtr device);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
            uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WriteFile(SafeFileHandle file, byte[] buffer, int length, out int written, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReadFile(SafeFileHandle file, byte[] buffer, int length, out int read, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CancelIoEx(SafeFileHandle file, IntPtr overlapped);
    }

    // Keeps the last known battery level and refreshes it in the background so the ring never waits on the device.
    internal sealed class MouseBattery : IDisposable
    {
        internal const int Unknown = -1;
        private const int PollMs = 5 * 60 * 1000;
        private const int RefreshThrottleMs = 5000;
        private readonly BatterySource source;
        private readonly Func<DateTime> utcNow;
        private readonly Func<int> tickCount;
        private readonly SynchronizationContext context;
        private readonly System.Threading.Timer timer;
        private volatile BatteryReading reading;
        private volatile bool disposed;
        private int busy;
        private int lastRefresh;
        private bool loggedFailure;
        internal event Action Changed;

        internal MouseBattery() : this(MouseBatteryReader.Read, true)
        {
        }

        internal MouseBattery(BatterySource source, bool poll, Func<DateTime> utcNow = null, Func<int> tickCount = null)
        {
            this.source = source;
            this.utcNow = utcNow ?? delegate { return DateTime.UtcNow; };
            this.tickCount = tickCount ?? delegate { return Environment.TickCount; };
            reading = new BatteryReading(Unknown, BatteryRead.Pending, null, this.utcNow);
            context = new WindowsFormsSynchronizationContext();
            lastRefresh = unchecked(this.tickCount() - RefreshThrottleMs);
            timer = new System.Threading.Timer(delegate { Poll(); }, null,
                poll ? 0 : Timeout.Infinite, poll ? PollMs : Timeout.Infinite);
        }

        internal int Percent { get { return reading.Percent; } }
        internal BatteryReading Reading { get { return reading; } }

        internal void Refresh()
        {
            int now = tickCount();
            if (disposed || unchecked(now - lastRefresh) < RefreshThrottleMs) return;
            lastRefresh = now;
            ThreadPool.QueueUserWorkItem(delegate { Poll(); });
        }

        internal void Poll()
        {
            if (disposed || Interlocked.Exchange(ref busy, 1) == 1) return;
            try
            {
                int value;
                BatteryRead result;
                DateTime updated = DateTime.MinValue;
                // This runs on a thread-pool thread: an escaped exception would end the whole app.
                try
                {
                    result = source(out value);
                    if ((result != BatteryRead.Ok && result != BatteryRead.Asleep && result != BatteryRead.Missing) ||
                        (result == BatteryRead.Ok && (value < 0 || value > 100)))
                        throw new InvalidOperationException("The battery source returned an invalid reading.");
                    updated = utcNow();
                    loggedFailure = false;
                }
                catch (Exception error)
                {
                    if (!loggedFailure) AppLog.Error("Battery read failed", error);
                    loggedFailure = true;
                    result = BatteryRead.Failed;
                    value = Unknown;
                }
                // A sleeping mouse keeps its last reading; an unplugged receiver clears it.
                BatteryReading previous = reading;
                int next = result == BatteryRead.Ok ? value : result == BatteryRead.Missing ? Unknown : previous.Percent;
                DateTime? timestamp = result == BatteryRead.Ok ? updated :
                    result == BatteryRead.Missing ? (DateTime?)null : previous.UpdatedUtc;
                if (next == previous.Percent && result == previous.State && timestamp == previous.UpdatedUtc) return;
                reading = new BatteryReading(next, result, timestamp, utcNow);
                if (disposed) return;
                try { context.Post(delegate { RaiseChanged(); }, null); }
                catch (InvalidOperationException error)
                {
                    if (!disposed) AppLog.Error("Dispatching battery status", error);
                }
            }
            finally { Interlocked.Exchange(ref busy, 0); }
        }

        private void RaiseChanged()
        {
            Action handler = Changed;
            if (!disposed && handler != null) handler();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            timer.Dispose();
        }
    }
}
