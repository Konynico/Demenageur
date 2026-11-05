using Demenageur.Core.FileSystem;
using Demenageur.Core.Journal;

namespace Demenageur.Core.Moving;

/// <summary>
/// Inscription du point de non-retour. Une fois « Committed » écrit sur le disque, plus aucun rollback n'est
/// permis. Si l'écriture échoue, on relit le journal pour savoir si elle a quand même eu lieu.
/// </summary>
internal static class CommitPoint
{
    /// <summary>
    /// Renvoie vrai si le point de non-retour est inscrit. Lève l'exception d'origine s'il n'est certainement PAS
    /// inscrit (le rollback est alors permis). Renvoie faux si l'on ne peut pas le savoir (aucune action ne doit suivre).
    /// </summary>
    public static bool TryCommit(IFileSystem fs, OperationJournal journal, string message, out Exception? undecided)
    {
        undecided = null;
        try
        {
            journal.Append(JournalSteps.Committed, message);
            return true;
        }
        catch (Exception ex) when (ex is FileSystemException or IOException or UnauthorizedAccessException)
        {
            JournalContent content;
            try
            {
                content = OperationJournal.Read(fs, journal.Path);
            }
            catch (Exception readError) when (readError is FileSystemException or IOException or UnauthorizedAccessException)
            {
                undecided = ex;
                return false;
            }

            if (content.Has(JournalSteps.Committed))
            {
                return true;
            }

            throw;
        }
    }
}
