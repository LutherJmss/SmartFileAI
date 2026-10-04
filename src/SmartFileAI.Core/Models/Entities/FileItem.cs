using System.IO;

namespace SmartFileAI.Core.Models.Entities;

public class FileItem
{
    public long Id { get; set; }
    
    /// <summary>
    /// 文件或文件夹名称 (带扩展名)
    /// </summary>
    public string Name { get; set; } = string.Empty;
    
    /// <summary>
    /// 完整绝对路径
    /// </summary>
    public string FullPath { get; set; } = string.Empty;
    
    /// <summary>
    /// 父级目录完整路径 (用于快速目录树分层与下钻查询)
    /// </summary>
    public string ParentPath { get; set; } = string.Empty;
    
    /// <summary>
    /// 是否为目录
    /// </summary>
    public bool IsDirectory { get; set; }
    
    /// <summary>
    /// 文件大小（字节）。若为目录，扫描初期置为 0，后续可汇总计算
    /// </summary>
    public long Size { get; set; }
    
    /// <summary>
    /// 文件扩展名 (统一小写，包含点号，例如: ".exe")；若为目录则为空字符串
    /// </summary>
    public string Extension { get; set; } = string.Empty;
    
    public DateTime CreatedTime { get; set; }
    public DateTime ModifiedTime { get; set; }
    public FileAttributes Attributes { get; set; }
}
