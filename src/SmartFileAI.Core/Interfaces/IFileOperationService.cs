using SmartFileAI.Core.Models.Enums;

namespace SmartFileAI.Core.Interfaces;

public interface IFileOperationService
{
    Task<OperationResultType> DeleteAsync(string fullPath, FileOperationType operationType);
    bool IsFileLocked(string fullPath);
}
