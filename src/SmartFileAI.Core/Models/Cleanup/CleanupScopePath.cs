namespace SmartFileAI.Core.Models.Cleanup;

/// <summary>Lexical index scope validation. This is not deletion authorization or a disk-existence check.</summary>
public static class CleanupScopePath
{
    public const int MaximumPageSize = 1024;

    public static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Any(char.IsControl) || path.StartsWith(@"\\", StringComparison.Ordinal)
            || path.StartsWith("//", StringComparison.Ordinal))
            throw new ArgumentException("范围必须是本地绝对路径，不支持网络或设备路径。", nameof(path));

        // Explicit Windows parsing also lets platform-neutral SQLite tests exercise actual Windows index paths.
        if (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':')
        {
            string windowsPath = path.Replace('/', '\\');
            if (windowsPath.Length < 3 || windowsPath[2] != '\\'
                || windowsPath[2..].IndexOfAny([':', '*', '?', '"', '<', '>', '|']) >= 0)
                throw new ArgumentException("范围必须是有效的驱动器绝对路径。", nameof(path));
            var parts = new List<string>();
            foreach (string part in windowsPath[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                if (part == ".") continue;
                if (part == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
                if (part.EndsWith(' ') || part.EndsWith('.'))
                    throw new ArgumentException("不支持末尾空格或点的路径分段。", nameof(path));
                parts.Add(part);
            }
            return windowsPath[..3] + string.Join("\\", parts);
        }

        if (!Path.IsPathFullyQualified(path) || path.Contains(':')
            || path.IndexOfAny(['*', '?', '"', '<', '>', '|']) >= 0)
            throw new ArgumentException("范围必须是有效的绝对路径。", nameof(path));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    public static string DescendantPrefix(string normalizedScope)
        => normalizedScope.EndsWith('\\') || normalizedScope.EndsWith('/')
            ? normalizedScope : normalizedScope + (normalizedScope.Length > 1 && normalizedScope[1] == ':'
                ? '\\' : Path.DirectorySeparatorChar);
}
