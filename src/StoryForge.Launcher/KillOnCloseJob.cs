using System.Diagnostics;
using System.Runtime.InteropServices;

namespace StoryForge.Launcher;

// 把子程序（後端 StoryForge.Web）放進設定了 KILL_ON_JOB_CLOSE 的 Windows Job Object。
// Job 的 handle 只由 Launcher 持有：Launcher 不論怎麼結束——正常結束、當掉、被工作管理員強制結束——
// handle 都會被系統關閉，Windows 隨即結束 Job 裡的所有程序，後端不會再被留下來鎖住 publish 資料夾。
// 這是保底機制，不取代 Launcher 在視窗關閉後主動結束後端的正常流程。
internal static class KillOnCloseJob
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    // 整個 Launcher 生命週期都不能釋放，否則 Job 會立刻關閉並結束後端
    private static IntPtr _job = IntPtr.Zero;

    public static void Assign(Process process)
    {
        if (_job == IntPtr.Zero)
        {
            _job = CreateJobObject(IntPtr.Zero, null);
            if (_job == IntPtr.Zero)
                throw new InvalidOperationException($"CreateJobObject 失敗（{Marshal.GetLastWin32Error()}）");

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
                },
            };

            var length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(info, buffer, false);
                if (!SetInformationJobObject(_job, JobObjectExtendedLimitInformation, buffer, (uint) length))
                    throw new InvalidOperationException($"SetInformationJobObject 失敗（{Marshal.GetLastWin32Error()}）");
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        if (!AssignProcessToJobObject(_job, process.Handle))
            throw new InvalidOperationException($"AssignProcessToJobObject 失敗（{Marshal.GetLastWin32Error()}）");
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int jobObjectInfoClass, IntPtr lpJobObjectInfo,
        uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
