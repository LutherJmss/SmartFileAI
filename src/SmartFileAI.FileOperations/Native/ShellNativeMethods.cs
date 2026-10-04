using System;
using System.Runtime.InteropServices;

namespace SmartFileAI.FileOperations.Native;

internal static class ShellNativeMethods
{
    public const uint FO_DELETE = 0x0003;

    public const ushort FOF_SILENT = 0x0004;
    public const ushort FOF_NOCONFIRMATION = 0x0010;
    public const ushort FOF_ALLOWUNDO = 0x0040;
    public const ushort FOF_NOERRORUI = 0x0400;
    public const ushort FOF_WANTNUKEWARNING = 0x4000;

    // Shell 特定错误返回代码
    public const int ERROR_SUCCESS = 0x0;
    public const int ERROR_FILE_NOT_FOUND = 0x2;
    public const int ERROR_PATH_NOT_FOUND = 0x3;
    public const int ERROR_SHARING_VIOLATION = 0x20;
    public const int DE_FILENOTFOUND = 0x74;
    public const int DE_OPCANCELLED = 0x75;
    public const int DE_ACCESSDENIEDSRC = 0x78;
    public const int DE_PATHTOODEEP = 0x79;
    public const int DE_INVALIDFILES = 0x7A;
    public const int DE_DESTSAMETREE = 0x7C;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", EntryPoint = "SHFileOperationW", CharSet = CharSet.Unicode)]
    internal static extern int SHFileOperationW(ref SHFILEOPSTRUCT lpFileOp);
}
