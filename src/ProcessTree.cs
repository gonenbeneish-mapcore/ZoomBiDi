using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ZoomBiDi;

/// <summary>Parent/child process lookups (used from the UI Automation thread only).</summary>
internal static class ProcessTree
{
    // (child, ancestor) -> answer. PIDs can be reused, but a stale "yes" only means we look at one more text box.
    static readonly Dictionary<(uint, uint), bool> Cache = new();

    public static bool IsSameOrDescendant(uint pid, uint ancestor)
    {
        if (pid == ancestor) return true;
        if (Cache.TryGetValue((pid, ancestor), out var cached)) return cached;

        var parents = Snapshot();
        bool result = false;
        uint current = pid;
        for (int depth = 0; depth < 8 && parents.TryGetValue(current, out var parent) && parent != 0; depth++)
        {
            if (parent == ancestor) { result = true; break; }
            if (parent == current) break;
            current = parent;
        }

        if (Cache.Count > 512) Cache.Clear();
        Cache[(pid, ancestor)] = result;
        return result;
    }

    static Dictionary<uint, uint> Snapshot()
    {
        var map = new Dictionary<uint, uint>();
        var snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == INVALID_HANDLE_VALUE) return map;
        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (Process32First(snap, ref entry))
            {
                do map[entry.th32ProcessID] = entry.th32ParentProcessID;
                while (Process32Next(snap, ref entry));
            }
        }
        finally
        {
            CloseHandle(snap);
        }
        return map;
    }

    const uint TH32CS_SNAPPROCESS = 0x00000002;
    static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")]
    static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")]
    static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr hObject);
}
