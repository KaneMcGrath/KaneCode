using System;
using System.Collections.Generic;

namespace KaneCode.Services;

/// <summary>
/// Classifies an edited line for git gutter / diff display.
/// </summary>
internal enum GitLineChangeType
{
    Added,
    Modified,
    Deleted
}

/// <summary>
/// A change marker for a single 1-based line of one side of a diff.
/// </summary>
internal sealed record GitLineChange(int LineNumber, GitLineChangeType ChangeType);

/// <summary>
/// Per-side change markers for a side-by-side diff.
/// Left markers reference line numbers in the left (original) text; right markers
/// reference line numbers in the right (modified) text.
/// </summary>
internal sealed record GitLineDiffResult(
    IReadOnlyList<GitLineChange> LeftChanges,
    IReadOnlyList<GitLineChange> RightChanges);

/// <summary>
/// Computes line-level differences between two versions of a file using the Myers
/// O(ND) algorithm, so an insertion or deletion in the middle of a file no longer
/// misclassifies every following line as modified.
/// </summary>
internal static class GitLineDiff
{
    private enum EditKind
    {
        Equal,
        Insert,
        Delete
    }

    private readonly record struct EditOp(EditKind Kind, int OldIndex, int NewIndex);

    /// <summary>
    /// Returns change markers for the working-tree (right) side of a diff, keyed to the
    /// line numbers of <paramref name="currentText"/>. Used by the editor git gutter.
    /// Deleted lines are anchored to the current line where the deletion occurred.
    /// </summary>
    public static IReadOnlyList<GitLineChange> ComputeChanges(string headText, string currentText)
    {
        return ComputeSideChangesCore(headText, currentText, includeDeletedOnRight: true).RightChanges;
    }

    /// <summary>
    /// Returns change markers for both sides of a diff.
    /// Left markers reference line numbers in <paramref name="leftText"/> (deleted/modified);
    /// right markers reference line numbers in <paramref name="rightText"/> (added/modified).
    /// </summary>
    public static GitLineDiffResult ComputeSideChanges(string leftText, string rightText)
    {
        return ComputeSideChangesCore(leftText, rightText, includeDeletedOnRight: false);
    }

    private static GitLineDiffResult ComputeSideChangesCore(
        string leftText,
        string rightText,
        bool includeDeletedOnRight)
    {
        IReadOnlyList<string> leftLines = SplitLines(leftText);
        IReadOnlyList<string> rightLines = SplitLines(rightText);
        List<EditOp> ops = ComputeEditScript(leftLines, rightLines);

        var leftChanges = new List<GitLineChange>();
        var rightChanges = new List<GitLineChange>();
        int leftIndex = 0;
        int rightIndex = 0;
        int rightLineCount = rightLines.Count;
        int opIndex = 0;

        while (opIndex < ops.Count)
        {
            EditOp op = ops[opIndex];
            if (op.Kind == EditKind.Equal)
            {
                leftIndex++;
                rightIndex++;
                opIndex++;
                continue;
            }

            // Collect a hunk: the maximal run of non-equal operations.
            int hunkLeftStart = leftIndex;
            int hunkRightStart = rightIndex;
            int deleteCount = 0;
            int insertCount = 0;
            while (opIndex < ops.Count && ops[opIndex].Kind != EditKind.Equal)
            {
                if (ops[opIndex].Kind == EditKind.Delete)
                {
                    deleteCount++;
                }
                else
                {
                    insertCount++;
                }

                opIndex++;
            }

            if (deleteCount == insertCount)
            {
                // A replaced block of equal size: mark both sides as modified.
                for (int offset = 0; offset < deleteCount; offset++)
                {
                    leftChanges.Add(new GitLineChange(hunkLeftStart + 1 + offset, GitLineChangeType.Modified));
                }

                for (int offset = 0; offset < insertCount; offset++)
                {
                    rightChanges.Add(new GitLineChange(hunkRightStart + 1 + offset, GitLineChangeType.Modified));
                }
            }
            else
            {
                for (int offset = 0; offset < deleteCount; offset++)
                {
                    leftChanges.Add(new GitLineChange(hunkLeftStart + 1 + offset, GitLineChangeType.Deleted));
                }

                for (int offset = 0; offset < insertCount; offset++)
                {
                    rightChanges.Add(new GitLineChange(hunkRightStart + 1 + offset, GitLineChangeType.Added));
                }

                if (deleteCount > insertCount && includeDeletedOnRight)
                {
                    // Deletions have no physical line on the right side; anchor a single
                    // marker at the current line where the deletion occurred.
                    int anchor = Math.Clamp(hunkRightStart + 1, 1, Math.Max(rightLineCount, 1));
                    rightChanges.Add(new GitLineChange(anchor, GitLineChangeType.Deleted));
                }
            }

            leftIndex += deleteCount;
            rightIndex += insertCount;
        }

        return new GitLineDiffResult(leftChanges, rightChanges);
    }

    /// <summary>
    /// Splits text into lines matching AvalonEdit's line semantics: a trailing newline
    /// does not produce an extra empty line.
    /// </summary>
    private static IReadOnlyList<string> SplitLines(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        string normalized = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        if (normalized.EndsWith("\n", StringComparison.Ordinal))
        {
            normalized = normalized[..^1];
        }

        return normalized.Length == 0 ? [] : normalized.Split('\n');
    }

    /// <summary>
    /// Runs the Myers O(ND) algorithm and returns an edit script that transforms
    /// <paramref name="oldLines"/> into <paramref name="newLines"/> in forward order.
    /// </summary>
    private static List<EditOp> ComputeEditScript(IReadOnlyList<string> oldLines, IReadOnlyList<string> newLines)
    {
        int n = oldLines.Count;
        int m = newLines.Count;
        int max = n + m;

        if (max == 0)
        {
            return [];
        }

        int offset = max;
        int[] v = new int[(2 * max) + 1];
        v[offset + 1] = 0;
        List<int[]> trace = [];
        bool reachedEnd = false;

        for (int depth = 0; depth <= max; depth++)
        {
            trace.Add((int[])v.Clone());

            for (int k = -depth; k <= depth; k += 2)
            {
                int x;
                if (k == -depth || (k != depth && v[offset + k - 1] < v[offset + k + 1]))
                {
                    x = v[offset + k + 1];
                }
                else
                {
                    x = v[offset + k - 1] + 1;
                }

                int y = x - k;
                while (x < n && y < m && string.Equals(oldLines[x], newLines[y], StringComparison.Ordinal))
                {
                    x++;
                    y++;
                }

                v[offset + k] = x;
                if (x >= n && y >= m)
                {
                    reachedEnd = true;
                    break;
                }
            }

            if (reachedEnd)
            {
                break;
            }
        }

        // Backtrack through the saved v snapshots to recover the edit script in reverse.
        var ops = new List<EditOp>();
        int i = n;
        int j = m;
        for (int depth = trace.Count - 1; depth >= 0; depth--)
        {
            int[] vSnapshot = trace[depth];
            int k = i - j;
            int prevK;
            if (k == -depth || (k != depth && vSnapshot[offset + k - 1] < vSnapshot[offset + k + 1]))
            {
                prevK = k + 1;
            }
            else
            {
                prevK = k - 1;
            }

            int prevX = vSnapshot[offset + prevK];
            int prevY = prevX - prevK;

            // The snake between (prevX, prevY) and (i, j) consists of equal lines.
            while (i > prevX && j > prevY)
            {
                ops.Add(new EditOp(EditKind.Equal, i - 1, j - 1));
                i--;
                j--;
            }

            if (depth > 0)
            {
                if (i == prevX)
                {
                    ops.Add(new EditOp(EditKind.Insert, -1, j - 1));
                }
                else
                {
                    ops.Add(new EditOp(EditKind.Delete, i - 1, -1));
                }
            }

            i = prevX;
            j = prevY;
        }

        ops.Reverse();
        return ops;
    }
}
