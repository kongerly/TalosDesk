using System.IO;

namespace TalosDesk.Core.Configuration;

/// <summary>
/// 判定两个路径是否指向同一个文件，用于阻止以当前工作区文件为导出目标这类保护。
///
/// 单纯比较 <see cref="Path.GetFullPath(string)"/> 的字符串会被同一文件的别名绕过：目录联接和符号链接
/// 可以指向同一个文件却给出完全不同的路径文本。因此这里按路径的每一段解析实际目标，
/// 得到展开链接后的最终路径再比较。
///
/// 不比较硬链接的文件 ID，因此不同名称的硬链接不会在此被拒绝。导出另以同目录临时文件
/// 替换目标目录项，不会原地截断硬链接共享的数据；成功后导出别名与原工作区分离。
/// 路径检查与替换仍不是跨程序事务，不能防止其他程序并发替换目录或链接。
/// </summary>
internal static class WorkspaceFileIdentity
{
    /// <summary>
    /// 展开路径中每一段的符号链接或目录联接，返回可比较的最终绝对路径。
    /// 不存在的路径原样返回：尚未创建的目标没有链接可解析，链接在写入时按目标文件本身处理。
    /// </summary>
    public static string ResolveRealPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root)) return fullPath;

        var current = root;
        foreach (var segment in fullPath[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            var target = ResolveLink(current);
            if (target is not null) current = Path.GetFullPath(target);
        }

        return current;
    }

    /// <summary>
    /// 判断 <paramref name="candidate"/> 是否与 <paramref name="workspacePath"/> 指向同一个文件。
    /// 链接解析失败时不放行：无法确认不是同一个文件时，按会覆盖工作区处理。
    /// </summary>
    public static bool IsSameFile(string workspacePath, string candidate)
    {
        if (PathsMatch(workspacePath, candidate)) return true;
        try
        {
            return PathsMatch(ResolveRealPath(workspacePath), ResolveRealPath(candidate));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // 解析失败不能当作"不是同一个文件"：这条保护的作用是避免覆盖工作区，宁可拒绝。
            return true;
        }
    }

    private static bool PathsMatch(string left, string right) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 目录联接和符号链接必须按各自类型解析：对文件路径调用 <see cref="Directory.ResolveLinkTarget"/> 会返回 null。
    /// </summary>
    private static string? ResolveLink(string path)
    {
        if (Directory.Exists(path)) return Directory.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName;
        if (File.Exists(path)) return File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName;
        return null;
    }
}
