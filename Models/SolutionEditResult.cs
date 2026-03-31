using Microsoft.CodeAnalysis;

namespace KaneCode.Models;

/// <summary>
/// Describes the full set of document changes produced by a Roslyn code action or rename,
/// including changed, added, and removed documents.
/// </summary>
internal sealed record SolutionEditResult(
    IReadOnlyDictionary<string, string> ChangedFiles,
    IReadOnlyDictionary<string, string> AddedFiles,
    IReadOnlyList<string> RemovedFiles)
{
    /// <summary>
    /// Returns true when there are no changes of any kind.
    /// </summary>
    public bool IsEmpty => ChangedFiles.Count == 0 && AddedFiles.Count == 0 && RemovedFiles.Count == 0;

    /// <summary>
    /// Collects changed, added, and removed documents between two Roslyn solutions.
    /// </summary>
    public static async Task<SolutionEditResult> CollectFromSolutionChangesAsync(
        Solution originalSolution,
        Solution changedSolution,
        CancellationToken cancellationToken)
    {
        Dictionary<string, string> changed = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> added = new(StringComparer.OrdinalIgnoreCase);
        List<string> removed = [];

        foreach (var projectChanges in changedSolution.GetChanges(originalSolution).GetProjectChanges())
        {
            foreach (DocumentId docId in projectChanges.GetChangedDocuments())
            {
                cancellationToken.ThrowIfCancellationRequested();
                Document? changedDoc = changedSolution.GetDocument(docId);
                if (changedDoc?.FilePath is null)
                {
                    continue;
                }

                var text = await changedDoc.GetTextAsync(cancellationToken).ConfigureAwait(false);
                changed[changedDoc.FilePath] = text.ToString();
            }

            foreach (DocumentId docId in projectChanges.GetAddedDocuments())
            {
                cancellationToken.ThrowIfCancellationRequested();
                Document? addedDoc = changedSolution.GetDocument(docId);
                if (addedDoc?.FilePath is null)
                {
                    continue;
                }

                var text = await addedDoc.GetTextAsync(cancellationToken).ConfigureAwait(false);
                added[addedDoc.FilePath] = text.ToString();
            }

            foreach (DocumentId docId in projectChanges.GetRemovedDocuments())
            {
                cancellationToken.ThrowIfCancellationRequested();
                Document? removedDoc = originalSolution.GetDocument(docId);
                if (removedDoc?.FilePath is not null)
                {
                    removed.Add(removedDoc.FilePath);
                }
            }
        }

        return new SolutionEditResult(changed, added, removed);
    }
}
