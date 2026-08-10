using System.IO;
using System.Text;

namespace KaneCode.Services.Ai.Tools;

/// <summary>
/// Helpers for building rich, human-readable tool-result details that are
/// displayed in the chat panel's tool-call section on success but are never
/// sent back to the model (so context budget is not consumed by verbose output).
/// </summary>
internal static class ToolResultDetails
{
    /// <summary>Maximum number of changed lines rendered in a diff preview.</summary>
    public const int MaxDiffLines = 40;

    /// <summary>
    /// Returns a compact display path: relative to <paramref name="projectRoot"/>
    /// when the path is inside it, otherwise the full path. Falls back to the raw
    /// path when no root is supplied.
    /// </summary>
    internal static string GetDisplayPath(string resolvedPath, string? projectRoot)
    {
        ArgumentNullException.ThrowIfNull(resolvedPath);

        if (!string.IsNullOrWhiteSpace(projectRoot) && IsWithinRoot(resolvedPath, projectRoot))
        {
            string relative = Path.GetRelativePath(projectRoot, resolvedPath);
            if (!string.IsNullOrWhiteSpace(relative) && relative != ".")
            {
                return relative;
            }
        }

        return resolvedPath;
    }

    /// <summary>Formats a byte count as a compact, human-readable string (B / KB / MB).</summary>
    internal static string FormatByteSize(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        if (bytes < 1024L * 1024)
        {
            return $"{bytes / 1024.0:0.#} KB";
        }

        return $"{bytes / (1024.0 * 1024.0):0.#} MB";
    }

    /// <summary>
    /// Counts the number of lines in a text. An empty string counts as zero lines;
    /// a trailing newline does not add an extra empty line.
    /// </summary>
    internal static int CountLines(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        int newlineCount = 0;
        foreach (char c in text)
        {
            if (c == '\n')
            {
                newlineCount++;
            }
        }

        // A final newline terminates the last line rather than opening a new one.
        return text[^1] == '\n' ? newlineCount : newlineCount + 1;
    }

    /// <summary>
    /// Builds a compact, line-based diff (unified-style, changed lines only)
    /// between <paramref name="oldText"/> and <paramref name="newText"/>.
    /// Removed lines are prefixed with <c>-</c>, added lines with <c>+</c>.
    /// The output is capped at <see cref="MaxDiffLines"/> changed lines.
    /// Returns an empty string when there is no textual difference.
    /// </summary>
    internal static string BuildLineDiff(string oldText, string newText)
    {
        string[] oldLines = SplitLines(oldText);
        string[] newLines = SplitLines(newText);

        if (oldLines.Length == 0 && newLines.Length == 0)
        {
            return string.Empty;
        }

        const int MaxInputLines = 400;
        if (oldLines.Length > MaxInputLines || newLines.Length > MaxInputLines)
        {
            // Avoid a pathological DP table on very large inputs; fall back to a
            // compact summary of the change instead of a full per-line diff.
            return BuildTruncatedDiffSummary(oldLines, newLines);
        }

        int n = oldLines.Length;
        int m = newLines.Length;

        // Longest-common-subsequence length table (bottom-up).
        int[,] lcs = new int[n + 1, m + 1];
        for (int row = n - 1; row >= 0; row--)
        {
            for (int col = m - 1; col >= 0; col--)
            {
                lcs[row, col] = string.Equals(oldLines[row], newLines[col], StringComparison.Ordinal)
                    ? lcs[row + 1, col + 1] + 1
                    : Math.Max(lcs[row + 1, col], lcs[row, col + 1]);
            }
        }

        var sb = new StringBuilder();
        int changedCount = 0;
        int skippedCount = 0;
        int i = 0;
        int j = 0;

        while (i < n && j < m)
        {
            if (string.Equals(oldLines[i], newLines[j], StringComparison.Ordinal))
            {
                i++;
                j++;
                continue;
            }

            // Prefer a removal (matches standard diff ordering) when both moves
            // preserve the same LCS length.
            if (lcs[i + 1, j] >= lcs[i, j + 1])
            {
                if (AppendChangedLine(sb, "- ", oldLines[i], ref changedCount))
                {
                    i++;
                }
                else
                {
                    skippedCount++;
                    i++;
                }
            }
            else
            {
                if (AppendChangedLine(sb, "+ ", newLines[j], ref changedCount))
                {
                    j++;
                }
                else
                {
                    skippedCount++;
                    j++;
                }
            }
        }

        while (i < n)
        {
            if (AppendChangedLine(sb, "- ", oldLines[i], ref changedCount))
            {
                i++;
            }
            else
            {
                skippedCount++;
                i++;
            }
        }

        while (j < m)
        {
            if (AppendChangedLine(sb, "+ ", newLines[j], ref changedCount))
            {
                j++;
            }
            else
            {
                skippedCount++;
                j++;
            }
        }

        if (skippedCount > 0)
        {
            sb.AppendLine($"  … and {skippedCount} more changed line(s)");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Builds a short content preview (up to <paramref name="maxLines"/> lines)
    /// for a written file, so the user can verify the write at a glance.
    /// Returns null when the content is empty.
    /// </summary>
    internal static string? BuildContentPreview(string content, int maxLines = 5)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (maxLines < 1)
        {
            maxLines = 1;
        }

        if (string.IsNullOrEmpty(content))
        {
            return null;
        }

        string[] lines = SplitLines(content);
        if (lines.Length == 0)
        {
            return null;
        }

        int shown = Math.Min(lines.Length, maxLines);
        var sb = new StringBuilder();

        for (int index = 0; index < shown; index++)
        {
            sb.AppendLine($"{index + 1,4} | {lines[index]}");
        }

        if (lines.Length > shown)
        {
            sb.AppendLine($"  … {lines.Length - shown} more line(s)");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Appends a changed line to the diff. Returns false when the output is
    /// already at the cap so the caller can count the skipped lines.
    /// </summary>
    private static bool AppendChangedLine(StringBuilder sb, string prefix, string line, ref int changedCount)
    {
        if (changedCount >= MaxDiffLines)
        {
            return false;
        }

        sb.Append(prefix).AppendLine(line);
        changedCount++;
        return true;
    }

    private static string BuildTruncatedDiffSummary(string[] oldLines, string[] newLines)
    {
        var sb = new StringBuilder();
        int removed = oldLines.Length;
        int added = newLines.Length;

        if (removed > 0)
        {
            sb.AppendLine($"- ({removed} removed line(s))");
            AppendPreviewLines(sb, "- ", oldLines, 3);
        }

        if (added > 0)
        {
            if (removed > 0)
            {
                sb.AppendLine();
            }

            sb.AppendLine($"+ ({added} added line(s))");
            AppendPreviewLines(sb, "+ ", newLines, 3);
        }

        return sb.ToString().TrimEnd();
    }

    private static void AppendPreviewLines(StringBuilder sb, string prefix, string[] lines, int maxLines)
    {
        int shown = Math.Min(lines.Length, maxLines);
        for (int index = 0; index < shown; index++)
        {
            sb.Append(prefix).AppendLine(lines[index]);
        }

        if (lines.Length > shown)
        {
            sb.AppendLine($"{prefix}…");
        }
    }

    private static string[] SplitLines(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        return text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .TrimEnd('\n')
            .Split('\n');
    }

    private static bool IsWithinRoot(string candidatePath, string rootPath)
    {
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        string normalizedCandidate = Path.GetFullPath(candidatePath);

        return string.Equals(normalizedCandidate, normalizedRoot, comparison)
            || normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison)
            || normalizedCandidate.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, comparison);
    }
}
