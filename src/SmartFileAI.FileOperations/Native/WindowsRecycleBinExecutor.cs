using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using SmartFileAI.Core.Models.Enums;

namespace SmartFileAI.FileOperations.Native;

public sealed record RecycleExecutionResult(OperationResultType Result, string? ErrorMessage, int RawCode);

[ComImport]
[Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItem
{
    // Pointer interface, no managed methods required for passing to IFileOperation.
}

[ComImport]
[Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFileOperation
{
    [PreserveSig] int Advise(IntPtr pfops, out uint pdwCookie);
    [PreserveSig] int Unadvise(uint dwCookie);
    
    [PreserveSig] int SetOperationFlags(uint dwOperationFlags);
    [PreserveSig] int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string pszMessage);
    [PreserveSig] int SetProgressDialog(IntPtr popd);
    [PreserveSig] int SetProperties(IntPtr pproparray);
    [PreserveSig] int SetOwnerWindow(IntPtr hwndOwner);
    
    [PreserveSig] int ApplyPropertiesToItem(IShellItem psiItem);
    [PreserveSig] int ApplyPropertiesToItems(IntPtr punkItems);
    
    [PreserveSig] int RenameItem(IShellItem psiItem, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, IntPtr pfopsItem);
    [PreserveSig] int RenameItems(IntPtr pUnkItems, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName);
    
    [PreserveSig] int MoveItem(IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, IntPtr pfopsItem);
    [PreserveSig] int MoveItems(IntPtr punkItems, IShellItem psiDestinationFolder);
    
    [PreserveSig] int CopyItem(IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string pszCopyName, IntPtr pfopsItem);
    [PreserveSig] int CopyItems(IntPtr punkItems, IShellItem psiDestinationFolder);
    
    [PreserveSig] int DeleteItem(IShellItem psiItem, IntPtr pfopsItem);
    [PreserveSig] int DeleteItems(IntPtr punkItems);
    
    [PreserveSig] int NewItem(IShellItem psiDestinationFolder, uint dwFileAttributes, [MarshalAs(UnmanagedType.LPWStr)] string pszName, [MarshalAs(UnmanagedType.LPWStr)] string pszTemplateName, IntPtr pfopsItem);
    
    [PreserveSig] int PerformOperations();
    
    [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool pfAnyOperationsAborted);
}

[ComImport]
[Guid("3ad05575-8857-4850-9277-11b85bdb8e09")]
internal class FileOperationClass { }

public static class WindowsRecycleBinExecutor
{
    private const uint FOFX_RECYCLEONDELETE = 0x00080000;
    private const uint FOF_NOCONFIRMATION = 0x0010;
    private const uint FOF_SILENT = 0x0004;
    private const uint FOF_NOERRORUI = 0x0400;

    private const int ERROR_FILE_NOT_FOUND = 2;
    private const int ERROR_PATH_NOT_FOUND = 3;
    private const int ERROR_ACCESS_DENIED = 5;
    private const int ERROR_SHARING_VIOLATION = 0x20;
    private const int E_ABORT = unchecked((int)0x80004004);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        [In] ref Guid riid,
        out IShellItem ppv);

    public static RecycleExecutionResult Recycle(string fullPath)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new RecycleExecutionResult(OperationResultType.Failed, "Recycle Bin deletion is only supported on Windows.", -1);
        }

        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal) || fullPath.StartsWith("//", StringComparison.Ordinal))
        {
            return new RecycleExecutionResult(OperationResultType.Failed, "Recycle Bin deletion for UNC paths is not supported.", -1);
        }

        // COM operations require an STA thread.
        // If already in an STA context (like WPF UI), execute inline.
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            return ExecuteRecycleInSTA(fullPath);
        }

        // Otherwise, spawn an explicitly controlled STA worker.
        RecycleExecutionResult? workerResult = null;
        Thread staThread = new Thread(() =>
        {
            workerResult = ExecuteRecycleInSTA(fullPath);
        });

        staThread.SetApartmentState(ApartmentState.STA);
        staThread.IsBackground = true;
        staThread.Start();
        staThread.Join();

        return workerResult ?? new RecycleExecutionResult(OperationResultType.Failed, "STA thread failed to return a valid execution result.", -1);
    }

    private static RecycleExecutionResult ExecuteRecycleInSTA(string fullPath)
    {
        IFileOperation? fileOp = null;
        IShellItem? shellItem = null;

        try
        {
            // 1. Create COM Object
            fileOp = (IFileOperation)new FileOperationClass();
            
            // 2. Queue Strict Flags
            uint flags = FOFX_RECYCLEONDELETE | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI;
            int hr = fileOp.SetOperationFlags(flags);
            if (hr < 0) return HandleError("SetOperationFlags", hr);

            // 3. Create IShellItem mapped to target path
            Guid iidShellItem = typeof(IShellItem).GUID;
            hr = SHCreateItemFromParsingName(fullPath, IntPtr.Zero, ref iidShellItem, out shellItem);
            if (hr < 0) return HandleError("SHCreateItemFromParsingName", hr);

            // 4. Queue deletion
            hr = fileOp.DeleteItem(shellItem, IntPtr.Zero);
            if (hr < 0) return HandleError("DeleteItem", hr);
            
            // 5. Execute operations (Capturing HRESULT explicitly)
            int performHr = fileOp.PerformOperations();

            // 6. Mandatory Abort Verification (Regardless of performHr result)
            int abortHr = fileOp.GetAnyOperationsAborted(out bool aborted);

            // 7. Evaluate comprehensive state strictly (Fail-closed)
            if (abortHr < 0)
            {
                return HandleError("GetAnyOperationsAborted", abortHr);
            }

            if (aborted)
            {
                return new RecycleExecutionResult(
                    OperationResultType.Failed, 
                    "Recycle Bin operation was aborted by user or system. (Fail-closed: no permanent delete fallback)", 
                    E_ABORT);
            }

            if (performHr < 0)
            {
                return HandleError("PerformOperations", performHr);
            }

            // Only returned if performHr >= 0 AND abortHr >= 0 AND aborted == false
            return new RecycleExecutionResult(OperationResultType.Success, null, 0);
        }
        catch (Exception ex)
        {
            return new RecycleExecutionResult(
                OperationResultType.Failed, 
                $"Unexpected execution fault during STA recycle: {ex.Message}. Fail-closed.", 
                -1);
        }
        finally
        {
            // Deterministic COM memory release enforced within the STA scope.
            if (shellItem != null) Marshal.ReleaseComObject(shellItem);
            if (fileOp != null) Marshal.ReleaseComObject(fileOp);
        }
    }

    private static RecycleExecutionResult HandleError(string step, int hr)
    {
        OperationResultType failType = MapHResultToResultType(hr);
        return new RecycleExecutionResult(failType, $"COM error during {step}: HRESULT=0x{hr:X8}. Fail-closed.", hr);
    }

    private static OperationResultType MapHResultToResultType(int hresult)
    {
        int win32Error = hresult;
        if ((hresult & 0xFFFF0000) == unchecked((int)0x80070000))
        {
            win32Error = hresult & 0xFFFF;
        }

        return win32Error switch
        {
            ERROR_SHARING_VIOLATION => OperationResultType.FileLocked,
            ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND => OperationResultType.NotFound,
            ERROR_ACCESS_DENIED => OperationResultType.PermissionDenied,
            _ => (hresult == E_ABORT) ? OperationResultType.Failed : OperationResultType.Failed
        };
    }
}