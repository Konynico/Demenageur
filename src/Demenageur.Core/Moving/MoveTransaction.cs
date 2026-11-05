using Demenageur.Core.Copy;
using Demenageur.Core.FileSystem;
using Demenageur.Core.Journal;

namespace Demenageur.Core.Moving;

/// <summary>
/// Déplacement d'un dossier S vers T, protocole « verrou d'abord » :
/// <list type="number">
/// <item>T créé vide, accès privé ;</item>
/// <item>S renommé en S′ dans le même dossier : verrou atomique, impossible si un fichier est ouvert ;</item>
/// <item>inventaire de S′, copie vers T ;</item>
/// <item>vérification indépendante du contenu, puis permissions définitives et leur vérification ;</item>
/// <item>jonction S → T, relue ;</item>
/// <item>validation à travers la jonction → POINT DE NON-RETOUR ;</item>
/// <item>suppression de S′ d'après son manifeste (en cas d'échec : nettoyage différé, jamais de rollback).</item>
/// </list>
/// Avant le point de non-retour, toute erreur (ou une annulation) déclenche le rollback automatique.
/// À tout instant, au moins une copie complète existe : S′ jusqu'à l'étape 7, T vérifié dès l'étape 4.
/// </summary>
public sealed class MoveTransaction
{
    private readonly MoveEngineContext _context;

    public MoveTransaction(MoveEngineContext context)
    {
        _context = context;
    }

    public OperationResult Execute(MoveRequest request, Action<OperationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var operationId = _context.NewOperationId();
        var source = PathUtil.TrimTrailingSeparator(request.SourcePath);
        var destination = PathUtil.TrimTrailingSeparator(request.DestinationPath);
        var locked = OperationIds.LockedPath(source, operationId);
        var operationDirectory = _context.Store.DirectoryOf(operationId);
        var warnings = new List<string>();
        var raw = _context.FileSystem;
        var retry = _context.Retry;

        IFileSystem fs;
        OperationJournal journal;
        try
        {
            fs = new PathGuard(raw, _context.ScopeFactory.Create(raw, [source, locked, destination, operationDirectory]));
            fs.CreateDirectory(operationDirectory);
            journal = new OperationJournal(fs, PathUtil.Join(operationDirectory, OperationStore.JournalFileName), _context.Clock);
            journal.Append(JournalSteps.Planned, $"Déplacement de « {request.SoftwareName} » vers « {destination} ».", new Dictionary<string, string>
            {
                [JournalKeys.Kind] = nameof(OperationKind.Move),
                [JournalKeys.Software] = request.SoftwareName,
                [JournalKeys.Source] = source,
                [JournalKeys.Destination] = destination,
                [JournalKeys.Locked] = locked,
            });
        }
        catch (Exception ex) when (ex is FileSystemException or IOException or UnauthorizedAccessException)
        {
            return new OperationResult(operationId, OperationKind.Move, OperationOutcome.RolledBack, $"Préparation impossible, rien n'a été modifié : {ex.Message}", warnings, ex);
        }

        var facts = new MoveFacts(source, destination, locked);
        Manifest? manifest = null;
        void Report(string step, TransferProgress? transfer = null) => progress?.Invoke(new OperationProgress(step, transfer));

        _context.Hooks.EnterCriticalSection();
        try
        {
            // ── Préconditions (revérifiées au dernier moment) ──
            var sourceInfo = fs.GetEntry(source) ?? throw new OperationAbortedException($"« {source} » est introuvable.");
            if (sourceInfo.Kind != FsEntryKind.Directory)
            {
                throw new OperationAbortedException($"« {source} » n'est pas un dossier ordinaire (déjà déplacé ?).");
            }

            if (fs.GetEntry(destination) is not null)
            {
                throw new OperationAbortedException($"La destination « {destination} » existe déjà.");
            }

            if (fs.GetEntry(locked) is not null)
            {
                throw new OperationAbortedException($"« {locked} » existe déjà.");
            }

            var originalShortName = FindShortName(fs, source);

            // ── 1) Destination vide, privée ──
            Report(MoveSteps.CreateDestination);
            facts.DestinationIntent = true;
            journal.Append(JournalSteps.DestinationIntent, "Création de la destination.");
            facts.DestinationId = fs.CreateDirectory(destination, SecurityDescriptorTools.CreatePrivateDirectoryDescriptor());
            journal.Append(JournalSteps.DestinationCreated, null, new Dictionary<string, string> { [JournalKeys.DestinationId] = facts.DestinationId.Value.ToString() });

            // ── 2) Verrou ──
            Report(MoveSteps.CheckProcesses);
            cancellationToken.ThrowIfCancellationRequested();
            if (_context.Hooks.CheckBeforeLock(source) is { } refusal)
            {
                throw new OperationAbortedException(refusal);
            }

            Report(MoveSteps.Lock);
            facts.SourceId = sourceInfo.Identity;
            journal.Append(JournalSteps.LockIntent, null, new Dictionary<string, string> { [JournalKeys.SourceId] = sourceInfo.Identity.ToString() });
            try
            {
                retry.Execute(() => fs.Rename(source, sourceInfo.Identity, PathUtil.GetName(locked)), cancellationToken);
            }
            catch (FileSystemException ex) when (ex.Kind is FsErrorKind.AccessDenied or FsErrorKind.SharingViolation)
            {
                throw new OperationAbortedException("Un fichier du logiciel est encore ouvert (programme, service ou Explorateur) : le dossier ne peut pas être verrouillé. Rien n'a été modifié.", ex);
            }

            journal.Append(JournalSteps.Locked, $"Original verrouillé sous « {locked} ».");

            // ── 3) Inventaire et copie ──
            Report(MoveSteps.Inventory);
            var snapshot = new TreeScanner(fs).Scan(locked, ScanOptions.Full, cancellationToken: cancellationToken);
            if (snapshot.HasBlockingProblems)
            {
                throw new OperationAbortedException(snapshot.Problems.First(p => p.Severity == ScanProblemSeverity.Blocking).Message);
            }

            warnings.AddRange(snapshot.Problems.Where(p => p.Severity == ScanProblemSeverity.Warning).Select(p => p.Message));
            manifest = Manifest.FromSnapshot(snapshot);
            fs.WriteNewFile(PathUtil.Join(operationDirectory, OperationStore.ManifestFileName), ManifestSerializer.Serialize(manifest));
            journal.Append(JournalSteps.ManifestSaved, null, new Dictionary<string, string>
            {
                [JournalKeys.Files] = manifest.FileCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                [JournalKeys.Bytes] = manifest.TotalBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });

            Report(MoveSteps.Copy);
            var copier = new TreeCopier(fs, retry);
            copier.Copy(manifest, destination, facts.DestinationId.Value, p => Report(MoveSteps.Copy, p), cancellationToken);
            journal.Append(JournalSteps.Copied);

            // ── 4) Vérification indépendante, puis permissions ──
            Report(MoveSteps.VerifyContent);
            var verification = new TreeVerifier(fs).Verify(manifest, destination, new VerificationOptions(), p => Report(MoveSteps.VerifyContent, p), cancellationToken);
            if (!verification.Success)
            {
                throw new OperationAbortedException(verification.Summary);
            }

            warnings.AddRange(verification.Issues.Where(i => i.IsWarning).Select(i => $"{i.RelativePath} : {i.Message}"));
            fs.WriteNewFile(PathUtil.Join(operationDirectory, OperationStore.VerifiedManifestFileName), ManifestSerializer.Serialize(manifest));
            journal.Append(JournalSteps.ContentVerified, verification.Summary);

            Report(MoveSteps.Security);
            copier.ApplySecurity(manifest, destination, rootSecurity: null, cancellationToken);
            var securityCheck = new TreeVerifier(fs).Verify(manifest, destination, new VerificationOptions { CompareContent = false, CompareSecurity = true }, cancellationToken: cancellationToken);
            if (!securityCheck.Success)
            {
                throw new OperationAbortedException(securityCheck.Summary);
            }

            warnings.AddRange(copier.Warnings.Select(w => $"{w.RelativePath} : {w.Message}"));
            journal.Append(JournalSteps.SecurityVerified);

            // ── 5) Jonction ──
            Report(MoveSteps.CreateJunction);
            cancellationToken.ThrowIfCancellationRequested();
            journal.Append(JournalSteps.JunctionIntent);
            if (fs.GetEntry(source) is not null)
            {
                throw new OperationAbortedException($"Un dossier a été recréé à « {source} » pendant la copie.");
            }

            facts.JunctionId = fs.CreateReparsePoint(source, isDirectory: true, ReparsePointData.CreateJunction(destination));
            journal.Append(JournalSteps.JunctionCreated, null, new Dictionary<string, string> { [JournalKeys.JunctionId] = facts.JunctionId.Value.ToString() });
            ApplyJunctionMetadata(fs, source, facts.JunctionId.Value, manifest.Root, originalShortName, warnings);

            // ── 6) Validation à travers la jonction ──
            Report(MoveSteps.ValidateJunction);
            if (!MoveProcedures.IsJunctionTo(fs, source, facts.JunctionId.Value, destination))
            {
                throw new OperationAbortedException("La jonction relue ne pointe pas vers la copie.");
            }

            var through = new TreeVerifier(fs).Verify(manifest, source, new VerificationOptions { FollowRootLink = true, CompareContent = false }, cancellationToken: cancellationToken);
            if (!through.Success)
            {
                throw new OperationAbortedException("Contenu vu à travers la jonction incorrect : " + through.Summary);
            }

            if (!CommitPoint.TryCommit(fs, journal, "Point de non-retour : le logiciel fonctionne depuis la copie vérifiée.", out var undecided))
            {
                // Impossible de savoir si le point de non-retour est inscrit : on ne touche plus à rien.
                _context.Hooks.LeaveCriticalSection();
                return new OperationResult(operationId, OperationKind.Move, OperationOutcome.NeedsAttention,
                    "Le déplacement est vérifié (jonction en place), mais le journal n'a pas pu être mis à jour : la reprise terminera l'opération au prochain lancement.",
                    warnings, undecided);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _context.Hooks.LeaveCriticalSection();
            var reason = ex is OperationCanceledException ? "Opération annulée." : ex.Message;
            MoveProcedures.TryAppend(journal, JournalSteps.Info, "Erreur avant le point de non-retour : " + reason);
            var rollback = MoveProcedures.RollbackMove(fs, journal, facts, retry);
            return new OperationResult(
                operationId,
                OperationKind.Move,
                rollback.Success ? OperationOutcome.RolledBack : OperationOutcome.NeedsAttention,
                rollback.Success ? $"{reason} {rollback.Message}" : $"{reason} {rollback.Message}",
                warnings,
                ex);
        }

        _context.Hooks.LeaveCriticalSection();

        // ── 7) Après le point de non-retour : suppression de l'original verrouillé ──
        Report(MoveSteps.DeleteOriginal);
        var outcome = MoveProcedures.FinishMove(fs, journal, facts, manifest!, retry, out var message);
        return new OperationResult(operationId, OperationKind.Move, outcome, message, warnings);
    }

    private static string? FindShortName(IFileSystem fs, string path)
    {
        var parent = PathUtil.GetParent(path);
        if (parent is null || fs.GetEntry(parent) is not { } parentInfo)
        {
            return null;
        }

        var name = PathUtil.GetName(path);
        var child = fs.ListDirectory(parent, parentInfo.Identity).FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));
        return child?.ShortName is { Length: > 0 } s && !string.Equals(s, name, StringComparison.Ordinal) ? s : null;
    }

    /// <summary>La jonction reprend les dates, attributs, permissions et nom court du dossier d'origine (au mieux).</summary>
    private static void ApplyJunctionMetadata(IFileSystem fs, string junction, FileIdentity identity, ManifestEntry originalRoot, string? shortName, List<string> warnings)
    {
        try
        {
            fs.SetBasicInfo(junction, identity, originalRoot.Times, originalRoot.CopiedAttributeValue);
            if (originalRoot.SecurityDescriptor is { } sd)
            {
                try
                {
                    fs.SetSecurityDescriptor(junction, identity, sd, SecurityParts.All);
                }
                catch (FileSystemException)
                {
                    fs.SetSecurityDescriptor(junction, identity, sd, SecurityParts.Dacl);
                }
            }

            if (shortName is not null && !fs.TrySetShortName(junction, identity, shortName))
            {
                warnings.Add($"Nom court « {shortName} » de l'emplacement d'origine non conservé.");
            }
        }
        catch (FileSystemException ex) when (ex.Kind is not FsErrorKind.IdentityMismatch)
        {
            warnings.Add($"Métadonnées de la jonction non recopiées : {ex.Message}");
        }
    }
}
