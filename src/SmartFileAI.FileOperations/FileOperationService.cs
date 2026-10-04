using System;
using System.IO;
using System.Threading.Tasks;
using SmartFileAI.Core.Interfaces;
using SmartFileAI.Core.Models.Entities;
using SmartFileAI.Core.Models.Enums;
using SmartFileAI.FileOperations.Native;
using SmartFileAI.FileOperations.Safety;

namespace SmartFileAI.FileOperations;

public sealed class FileOperationService : IFileOperationService
{
    private readonly IFileRepository? _repository;

    public FileOperationService(IFileRepository? repository = null) => _repository = repository;

    public bool IsFileLocked(string fullPath) => IsFileLockedHeuristic(fullPath);

    public async Task<OperationResultType> DeleteAsync(string fullPath, FileOperationType operationType)
    {
        // ----------------------------------------------------
        // 1. 第一次安全策略检查 (Pre-flight Safety Check)
        // ----------------------------------------------------
        var primaryCheck = DeletionSafetyPolicy.Evaluate(fullPath);
        if (!primaryCheck.IsPermitted)
        {
            var rejectResult = primaryCheck.Reason == DeletionRejectionReason.EmptyOrWhitespacePath 
                ? OperationResultType.NotFound 
                : OperationResultType.PermissionDenied;

            return await RecordAuditOnlyAsync(fullPath, operationType, rejectResult, primaryCheck.Details);
        }

        string normalized;
        try
        {
            normalized = Path.GetFullPath(fullPath);
        }
        catch (Exception ex)
        {
            return await RecordAuditOnlyAsync(fullPath, operationType, OperationResultType.Failed, $"Path normalization failed: {ex.Message}");
        }

        // 检查物理对象是否存在
        if (!File.Exists(normalized) && !Directory.Exists(normalized))
        {
            return await RecordAuditOnlyAsync(normalized, operationType, OperationResultType.NotFound, "Target path does not exist on disk.");
        }

        // ----------------------------------------------------
        // 2. 启发式文件锁检测 (Heuristic check only, not absolute proof)
        // ----------------------------------------------------
        if (File.Exists(normalized) && IsFileLockedHeuristic(normalized))
        {
            return await RecordAuditOnlyAsync(normalized, operationType, OperationResultType.FileLocked, "Target file is currently locked or in use by another process (heuristic).");
        }

        // ----------------------------------------------------
        // 3. 第二次安全策略检查 (TOCTOU Double Safety Check)
        // 紧邻物理删除动作前再次确认目标及父链未被篡改/替换为软链接/重解析点
        // ----------------------------------------------------
        var secondaryCheck = DeletionSafetyPolicy.Evaluate(normalized);
        if (!secondaryCheck.IsPermitted)
        {
            return await RecordAuditOnlyAsync(normalized, operationType, OperationResultType.PermissionDenied, $"Secondary safety check failed: {secondaryCheck.Details}");
        }

        // ----------------------------------------------------
        // 4. 物理文件系统删除执行
        // ----------------------------------------------------
        OperationResultType physicalResult;
        string? executionError = null;

        try
        {
            if (operationType == FileOperationType.PermanentDelete)
            {
                physicalResult = ExecutePermanentDelete(normalized, out executionError);
            }
            else if (operationType == FileOperationType.RecycleDelete)
            {
                var recycleRes = WindowsRecycleBinExecutor.Recycle(normalized);
                physicalResult = recycleRes.Result;
                executionError = recycleRes.ErrorMessage;
            }
            else
            {
                physicalResult = OperationResultType.Failed;
                executionError = $"Unsupported operation type: {operationType}";
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            physicalResult = OperationResultType.PermissionDenied;
            executionError = ex.Message;
        }
        catch (IOException ex)
        {
            physicalResult = IsFileLockedHeuristic(normalized) ? OperationResultType.FileLocked : OperationResultType.Failed;
            executionError = ex.Message;
        }
        catch (Exception ex)
        {
            physicalResult = OperationResultType.Failed;
            executionError = ex.Message;
        }

        // ----------------------------------------------------
        // 5. 校验物理删除事实，并保证数据库一致性
        // ----------------------------------------------------
        bool physicallyExistsAfterOperation = File.Exists(normalized) || Directory.Exists(normalized);

        // 如果物理删除失败或者目标依然残留
        if (physicalResult != OperationResultType.Success || physicallyExistsAfterOperation)
        {
            var finalFailType = physicalResult == OperationResultType.Success 
                ? OperationResultType.Failed 
                : physicalResult;

            string finalError = executionError ?? "Physical deletion did not complete: target still exists on disk.";
            return await RecordAuditOnlyAsync(normalized, operationType, finalFailType, finalError);
        }

        // 物理删除确认成功！现在推进数据库与日志同步
        return await SynchronizeDatabaseAfterPhysicalSuccessAsync(normalized, operationType);
    }

    private async Task<OperationResultType> RecordAuditOnlyAsync(
        string path, 
        FileOperationType operation, 
        OperationResultType result, 
        string? error)
    {
        if (_repository is not null)
        {
            try
            {
                await _repository.LogOperationAsync(new OperationLog
                {
                    Path = path,
                    Operation = operation,
                    Result = result,
                    ErrorMessage = error,
                    Time = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to write audit log for failed operation: {ex.Message}");
            }
        }

        return result;
    }

    private async Task<OperationResultType> SynchronizeDatabaseAfterPhysicalSuccessAsync(
        string normalizedPath, 
        FileOperationType operation)
    {
        bool dbSyncFailed = false;
        bool logSyncFailed = false;
        string? syncErrorMessage = null;

        if (_repository is not null)
        {
            // 步骤 1: 同步移除数据库索引
            try
            {
                await _repository.DeleteFileRecordAsync(normalizedPath);
            }
            catch (Exception ex)
            {
                dbSyncFailed = true;
                syncErrorMessage = $"IndexSyncFailed: {ex.Message}";
                System.Diagnostics.Debug.WriteLine($"Failed to delete file record from DB: {ex.Message}");
            }

            // 步骤 2: 记录操作审计日志
            try
            {
                await _repository.LogOperationAsync(new OperationLog
                {
                    Path = normalizedPath,
                    Operation = operation,
                    Result = dbSyncFailed ? OperationResultType.SuccessWithWarning : OperationResultType.Success,
                    ErrorMessage = syncErrorMessage,
                    Time = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                logSyncFailed = true;
                syncErrorMessage = syncErrorMessage is null 
                    ? $"AuditLogFailed: {ex.Message}" 
                    : $"{syncErrorMessage}; AuditLogFailed: {ex.Message}";
                System.Diagnostics.Debug.WriteLine($"Failed to record successful operation log: {ex.Message}");
            }
        }

        // 如果物理删除成功，但索引同步或审计日志至少一个失败，必须返回 SuccessWithWarning！
        if (dbSyncFailed || logSyncFailed)
        {
            return OperationResultType.SuccessWithWarning;
        }

        return OperationResultType.Success;
    }

    private static OperationResultType ExecutePermanentDelete(string fullPath, out string? errorMessage)
    {
        errorMessage = null;

        if (Directory.Exists(fullPath))
        {
            var info = new DirectoryInfo(fullPath);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                errorMessage = "ReparsePoint directory detected during permanent delete attempt.";
                return OperationResultType.PermissionDenied;
            }

            ClearReadOnlyAttributesSafely(info);
            Directory.Delete(fullPath, recursive: true);
        }
        else
        {
            var file = new FileInfo(fullPath);
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                errorMessage = "ReparsePoint file detected during permanent delete attempt.";
                return OperationResultType.PermissionDenied;
            }

            if ((file.Attributes & FileAttributes.ReadOnly) != 0)
            {
                file.Attributes &= ~FileAttributes.ReadOnly;
            }

            File.Delete(fullPath);
        }

        return OperationResultType.Success;
    }

    private static void ClearReadOnlyAttributesSafely(DirectoryInfo dir)
    {
        try
        {
            if ((dir.Attributes & FileAttributes.ReadOnly) != 0)
                dir.Attributes &= ~FileAttributes.ReadOnly;

            foreach (var file in dir.EnumerateFiles())
            {
                try
                {
                    if ((file.Attributes & FileAttributes.ReparsePoint) == 0 &&
                        (file.Attributes & FileAttributes.ReadOnly) != 0)
                    {
                        file.Attributes &= ~FileAttributes.ReadOnly;
                    }
                }
                catch
                {
                }
            }

            foreach (var sub in dir.EnumerateDirectories())
            {
                try
                {
                    if ((sub.Attributes & FileAttributes.ReparsePoint) == 0)
                    {
                        ClearReadOnlyAttributesSafely(sub);
                    }
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }

    private static bool IsFileLockedHeuristic(string fullPath)
    {
        try
        {
            using var stream = File.Open(fullPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }
}
