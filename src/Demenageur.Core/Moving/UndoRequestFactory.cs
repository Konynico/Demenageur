using Demenageur.Core.Journal;

namespace Demenageur.Core.Moving;

/// <summary>Construit la demande d'annulation d'un déplacement terminé, à partir de son journal et de son manifeste.</summary>
public static class UndoRequestFactory
{
    public static UndoRequest FromMove(OperationStore store, StoredOperation move)
    {
        if (move.Kind != OperationKind.Move || !move.IsReadable || !move.IsCommitted)
        {
            throw new InvalidOperationException("Seul un déplacement arrivé au point de non-retour peut être annulé.");
        }

        var destinationId = move.IdentityFrom(JournalSteps.DestinationCreated, JournalKeys.DestinationId)
                            ?? throw new InvalidOperationException("Identité de la destination absente du journal.");
        var manifest = store.LoadManifest(move.Id, OperationStore.ManifestFileName);
        return new UndoRequest
        {
            MoveOperationId = move.Id,
            SoftwareName = move.SoftwareName,
            SourcePath = move.SourcePath!,
            DestinationPath = move.DestinationPath!,
            DestinationIdentity = destinationId,
            OriginalRootSecurity = manifest?.Root.SecurityDescriptor,
        };
    }
}
