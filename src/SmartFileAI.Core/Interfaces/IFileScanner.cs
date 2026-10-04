using SmartFileAI.Core.Models;
using SmartFileAI.Core.Models.Entities;

namespace SmartFileAI.Core.Interfaces;

public interface IFileScanner
{
    /// <summary>
    /// 异步流式扫描指定根路径，逐批产出文件项并支持取消与进度同步
    /// </summary>
    IAsyncEnumerable<FileItem> ScanDirectoryAsync(
        string rootPath,
        IProgress<ScanProgressReport>? progress = null,
        CancellationToken cancellationToken = default
    );
}
