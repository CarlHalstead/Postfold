using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.UpdateBroker.Services;

internal static class NativeApplyProcess
{
    internal static async Task<int> RunAsync(
        string helper,
        string root,
        string package,
        string log,
        IReadOnlyList<SafeFileHandle> locks,
        Action<string> observe,
        CancellationToken cancellationToken
    )
    {
        // Only the verified full package is ever named. Never route through UpdateExe.Apply's fallback.
        string command = $"\"{helper}\" --silent --log \"{log}\" apply --norestart --package \"{package}\" --rootDir \"{root}\" --packageDir \"{System.IO.Path.GetDirectoryName(package)}\"";
        using SafeFileHandle job = CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        IntPtr limits = Marshal.AllocHGlobal(144);
        IntPtr attributes = IntPtr.Zero;
        IntPtr inherited = IntPtr.Zero;
        IntPtr childPolicy = IntPtr.Zero;
        IntPtr jobList = IntPtr.Zero;
        ProcessInformation information = default;
        Process? process = null;
        try
        {
            Marshal.Copy(new byte[144], 0, limits, 144);
            // No helper descendants, runtime installers, elevated consumers or lifecycle hooks in this boundary.
            Marshal.WriteInt32(limits, 16, 0x2008); // KILL_ON_JOB_CLOSE | ACTIVE_PROCESS; no breakaway.
            Marshal.WriteInt32(limits, 40, 1);
            if (!SetInformationJobObject(job, 9, limits, 144))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            nuint size = 0;
            InitializeProcThreadAttributeList(IntPtr.Zero, 3, 0, ref size);
            attributes = Marshal.AllocHGlobal(checked((int)size));
            if (!InitializeProcThreadAttributeList(attributes, 3, 0, ref size))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            IntPtr[] handles = locks.Select(handle => handle.DangerousGetHandle()).ToArray();
            inherited = Marshal.AllocHGlobal(handles.Length * IntPtr.Size);
            Marshal.Copy(handles, 0, inherited, handles.Length);
            if (!UpdateProcThreadAttribute(attributes, 0, (nuint)0x20002, inherited, (nuint)(handles.Length * IntPtr.Size), IntPtr.Zero, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            childPolicy = Marshal.AllocHGlobal(4);
            Marshal.WriteInt32(childPolicy, 1); // PROCESS_CREATION_CHILD_PROCESS_RESTRICTED.
            if (!UpdateProcThreadAttribute(attributes, 0, (nuint)0x2000E, childPolicy, 4, IntPtr.Zero, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            jobList = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(jobList, job.DangerousGetHandle());
            if (!UpdateProcThreadAttribute(attributes, 0, (nuint)0x2000D, jobList, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            StartupInformation startup = new StartupInformation
            {
                Size = Marshal.SizeOf<StartupInformation>(),
                Attributes = attributes
            };
            cancellationToken.ThrowIfCancellationRequested();
            if (!CreateProcessW(helper, new StringBuilder(command), IntPtr.Zero, IntPtr.Zero, true, 0x08080004, IntPtr.Zero, System.IO.Path.GetDirectoryName(helper), ref startup, out information))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            process = Process.GetProcessById(information.ProcessId);
            observe("helper:" + process.Id + ":" + process.StartTime.ToUniversalTime().Ticks);
            // Job membership and lock inheritance are atomic with creation, including broker death before resume.
            if (ResumeThread(information.Thread) == uint.MaxValue)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode;
        }
        finally
        {
            try
            {
                if (information.Process != IntPtr.Zero)
                {
                    // Covers assignment failure too; never leave a suspended orphan.
                    try
                    {
                        TerminateProcess(information.Process, 1);
                        TerminateJobObject(job, 1);
                        WaitForSingleObject(information.Process, 10000);
                        await ObserveJobExitAsync(job, observe).ConfigureAwait(false);
                    }
                    finally
                    {
                        CloseHandle(information.Process);
                        CloseHandle(information.Thread);
                    }
                }
            }
            finally
            {
                process?.Dispose();
                if (attributes != IntPtr.Zero)
                {
                    DeleteProcThreadAttributeList(attributes);
                    Marshal.FreeHGlobal(attributes);
                }
                if (inherited != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(inherited);
                }
                if (childPolicy != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(childPolicy);
                }
                if (jobList != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(jobList);
                }
                Marshal.FreeHGlobal(limits);
            }
        }
    }

    private static async Task ObserveJobExitAsync(SafeFileHandle job, Action<string> observe)
    {
        IntPtr accounting = Marshal.AllocHGlobal(48);
        try
        {
            long started = Stopwatch.GetTimestamp();
            while (true)
            {
                if (!QueryInformationJobObject(job, 1, accounting, 48, IntPtr.Zero))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                if (Marshal.ReadInt32(accounting, 40) == 0)
                {
                    observe("job-exited:total=" + Marshal.ReadInt32(accounting, 36) + ":active=0");
                    return;
                }
                if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(10))
                {
                    throw new IOException("Native apply job did not acknowledge termination.");
                }
                await Task.Delay(TimeSpan.FromMilliseconds(20), CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(accounting);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int kind, IntPtr information, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int kind, IntPtr information, uint length, IntPtr returned);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref nuint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, nuint attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returnedSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string application, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment, string? directory, ref StartupInformation startup, out ProcessInformation information);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint code);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInformation
    {
        internal int Size;
        internal IntPtr Reserved;
        internal IntPtr Desktop;
        internal IntPtr Title;
        internal int X;
        internal int Y;
        internal int XSize;
        internal int YSize;
        internal int XChars;
        internal int YChars;
        internal int Fill;
        internal int Flags;
        internal short Show;
        internal short ReservedSize;
        internal IntPtr ReservedPointer;
        internal IntPtr Input;
        internal IntPtr Output;
        internal IntPtr Error;
        internal IntPtr Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        internal IntPtr Process;
        internal IntPtr Thread;
        internal int ProcessId;
        internal int ThreadId;
    }
}
