using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Demenageur.App.Services;
using Demenageur.Core.Abstractions;
using Demenageur.Core.Inventory;
using Demenageur.Core.Moving;
using Demenageur.Core.Safety;

namespace Demenageur.App.ViewModels;

public enum WizardStep
{
    Destination,
    Simulation,
    Running,
    Done,
}

/// <summary>Ligne d'étape (simulation puis exécution).</summary>
public sealed partial class StepLine : ObservableObject
{
    public StepLine(string title, string detail)
    {
        Title = title;
        Detail = detail;
    }

    public string Title { get; }

    public string Detail { get; }

    /// <summary>« à venir », « en cours », « terminé ».</summary>
    [ObservableProperty]
    private string state = "à venir";

    [ObservableProperty]
    private bool isCurrent;

    [ObservableProperty]
    private bool isDone;
}

/// <summary>Choix de disque de destination.</summary>
public sealed record DestinationChoice(DriveViewModel Drive, string Root)
{
    public string Display => $"{Drive.Letter} {Drive.Label} — {Drive.FreeText} ({Drive.Description})";
}

/// <summary>
/// Assistant de déplacement (ou d'annulation) :
/// destination et options → simulation (rapport, étapes, confirmation) → exécution → résultat.
/// </summary>
public sealed partial class OperationViewModel : ObservableObject
{
    private readonly Engine _engine;
    private readonly MoveCandidate? _candidate;
    private readonly IReadOnlyList<InstalledSoftware> _catalog;
    private readonly MovedSoftware? _moved;
    private CancellationTokenSource? _cancellation;
    private TaskCompletionSource<bool>? _restorePointDecision;
    private readonly Stopwatch _clock = new();

    /// <summary>Déplacement.</summary>
    public OperationViewModel(Engine engine, MoveCandidate candidate, IReadOnlyList<InstalledSoftware> catalog, IReadOnlyList<DriveViewModel> drives)
    {
        _engine = engine;
        _candidate = candidate;
        _catalog = catalog;
        IsMove = true;
        Title = $"Déplacer « {candidate.DisplayName} »";
        SourceText = candidate.Folder;
        var sourceLetter = candidate.Folder.Length >= 2 ? candidate.Folder[..2] : string.Empty;
        foreach (var drive in drives.Where(d => !string.Equals(d.Letter, sourceLetter, StringComparison.OrdinalIgnoreCase)))
        {
            Destinations.Add(new DestinationChoice(drive, System.IO.Path.Combine(drive.Letter + "\\", engine.Settings.DestinationFolderName)));
        }

        selectedDestination = Destinations.OrderByDescending(d => d.Drive.Free).FirstOrDefault();
        destinationRoot = selectedDestination?.Root ?? string.Empty;
        createRestorePoint = engine.Settings.RestorePointByDefault;
        RestorePointAvailability = engine.RestorePoints.GetAvailability();
        if (RestorePointAvailability == RestorePointAvailability.ProtectionDisabled)
        {
            createRestorePoint = false;
        }

        step = WizardStep.Destination;
    }

    /// <summary>Annulation d'un déplacement.</summary>
    public OperationViewModel(Engine engine, MovedSoftware moved)
    {
        _engine = engine;
        _moved = moved;
        _catalog = [];
        IsMove = false;
        Title = $"Annuler le déplacement de « {moved.SoftwareName} »";
        SourceText = $"{moved.Destination}  →  {moved.Source}";
        RestorePointAvailability = engine.RestorePoints.GetAvailability();
        step = WizardStep.Simulation;
    }

    public bool IsMove { get; }

    public string Title { get; }

    public string SourceText { get; }

    public string ExpectedName => _candidate?.DisplayName ?? _moved?.SoftwareName ?? string.Empty;

    public ObservableCollection<DestinationChoice> Destinations { get; } = [];

    public ObservableCollection<RuleVerdict> Verdicts { get; } = [];

    public ObservableCollection<StepLine> Steps { get; } = [];

    public ObservableCollection<string> Warnings { get; } = [];

    public RestorePointAvailability RestorePointAvailability { get; }

    public bool RestorePointPossible => RestorePointAvailability != RestorePointAvailability.ProtectionDisabled;

    public bool RestorePointImpossible => !RestorePointPossible;

    public string ExecuteLabel => IsMove ? "Déplacer maintenant" : "Annuler le déplacement maintenant";

    public bool SimulationOnly => _engine.Settings.SimulationOnly;

    public bool HasNoDestination => IsMove && Destinations.Count == 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDestinationStep), nameof(IsSimulationStep), nameof(IsRunningStep), nameof(IsDoneStep))]
    private WizardStep step;

    public bool IsDestinationStep => Step == WizardStep.Destination;

    public bool IsSimulationStep => Step == WizardStep.Simulation;

    public bool IsRunningStep => Step == WizardStep.Running;

    public bool IsDoneStep => Step == WizardStep.Done;

    [ObservableProperty]
    private DestinationChoice? selectedDestination;

    partial void OnSelectedDestinationChanged(DestinationChoice? value) => DestinationRoot = value?.Root ?? string.Empty;

    [ObservableProperty]
    private string destinationRoot = string.Empty;

    [ObservableProperty]
    private bool createRestorePoint;

    [ObservableProperty]
    private bool stopExplorer;

    [ObservableProperty]
    private bool stopServices;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private string busyText = string.Empty;

    [ObservableProperty]
    private string summary = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExecuteOperation))]
    private bool planAllowsExecution;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExecuteOperation))]
    private bool needsStrongConfirmation;

    [ObservableProperty]
    private bool offerExplorerOption;

    [ObservableProperty]
    private bool offerServicesOption;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExecuteOperation))]
    private bool understood;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExecuteOperation))]
    private string typedName = string.Empty;

    public bool CanExecuteOperation => PlanAllowsExecution && Understood && !SimulationOnly
        && (!NeedsStrongConfirmation || string.Equals(TypedName.Trim(), ExpectedName.Trim(), StringComparison.CurrentCultureIgnoreCase));

    [ObservableProperty]
    private string currentStep = string.Empty;

    [ObservableProperty]
    private double percent;

    [ObservableProperty]
    private string progressDetail = string.Empty;

    [ObservableProperty]
    private bool canCancel;

    [ObservableProperty]
    private bool askRestorePointDecision;

    [ObservableProperty]
    private string restorePointMessage = string.Empty;

    [ObservableProperty]
    private string resultTitle = string.Empty;

    [ObservableProperty]
    private string resultMessage = string.Empty;

    [ObservableProperty]
    private bool resultSuccess;

    /// <summary>Une opération réelle a eu lieu (la liste doit être rechargée).</summary>
    public bool Changed { get; private set; }

    public event Action? CloseRequested;

    private MovePlan? _plan;
    private UndoPlan? _undoPlan;

    [RelayCommand]
    public async Task SimulateAsync()
    {
        IsBusy = true;
        BusyText = IsMove ? "Simulation : analyse complète du dossier, sans rien modifier…" : "Vérifications avant l'annulation…";
        Verdicts.Clear();
        Steps.Clear();
        try
        {
            if (IsMove)
            {
                var request = new MovePlanRequest(_candidate!, DestinationRoot.Trim()) { StopExplorer = StopExplorer, StopServices = StopServices };
                _plan = await Task.Run(() => _engine.CreatePlanner().Plan(request, _catalog));
                Fill(_plan.Verdicts, _plan.Steps);
                Summary = $"Taille : {MovePlanner.Bytes(_plan.LogicalBytes)} · espace requis (+10 %) : {MovePlanner.Bytes(_plan.RequiredBytes)} · disponible : {MovePlanner.Bytes(_plan.FreeBytes)} · durée estimée : {Duration(_plan.EstimatedDuration)}"
                          + $"\nDestination : {_plan.Destination}";
                PlanAllowsExecution = _plan.CanExecute;
                NeedsStrongConfirmation = _plan.RequiresStrongConfirmation;
                OfferExplorerOption = _plan.ExplorerIsOnlyBlocker || StopExplorer;
                OfferServicesOption = _plan.RunningServices.Count > 0 || StopServices;
            }
            else
            {
                _undoPlan = await Task.Run(() => _engine.PlanUndo(_moved!));
                Fill(_undoPlan.Verdicts, _undoPlan.Steps);
                Summary = $"Taille actuelle : {MovePlanner.Bytes(_undoPlan.CurrentBytes)} · espace requis sur le disque d'origine (+10 %) : {MovePlanner.Bytes(_undoPlan.RequiredBytes)} · disponible : {MovePlanner.Bytes(_undoPlan.FreeBytes)}";
                PlanAllowsExecution = _undoPlan.CanExecute;
            }

            if (_engine.Power.IsOnBattery(out var battery))
            {
                Verdicts.Add(new RuleVerdict("R-BATTERIE", RuleLevel.Warning, "Sur batterie", $"L'ordinateur fonctionne sur batterie ({(battery >= 0 ? battery + " %" : "niveau inconnu")}) : branche le secteur avant de continuer."));
            }

            if (Verdicts.Count == 0 || Verdicts.All(v => v.Level == RuleLevel.Info))
            {
                Verdicts.Insert(0, new RuleVerdict("OK", RuleLevel.Info, "Aucun obstacle", "Toutes les vérifications sont passées."));
            }

            Step = WizardStep.Simulation;
        }
        catch (Exception ex)
        {
            Verdicts.Add(new RuleVerdict("ERREUR", RuleLevel.Block, "Simulation impossible", ex.Message));
            PlanAllowsExecution = false;
            Step = WizardStep.Simulation;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Fill(IEnumerable<RuleVerdict> verdicts, IEnumerable<(string Step, string Detail)> steps)
    {
        foreach (var verdict in verdicts.OrderByDescending(v => v.Level))
        {
            Verdicts.Add(verdict);
        }

        foreach (var (title, detail) in steps)
        {
            Steps.Add(new StepLine(title, detail));
        }
    }

    [RelayCommand]
    private void Back()
    {
        if (IsMove)
        {
            Step = WizardStep.Destination;
            Understood = false;
            TypedName = string.Empty;
        }
    }

    [RelayCommand]
    private void OpenSystemProtection() => _engine.RestorePoints.OpenSystemProtectionSettings();

    [RelayCommand]
    private void RestorePointContinue() => _restorePointDecision?.TrySetResult(true);

    [RelayCommand]
    private void RestorePointAbort() => _restorePointDecision?.TrySetResult(false);

    [RelayCommand]
    public async Task ExecuteAsync()
    {
        if (!CanExecuteOperation)
        {
            return;
        }

        Step = WizardStep.Running;
        _engine.OperationInProgress = true;
        _cancellation = new CancellationTokenSource();
        CanCancel = true;
        _clock.Restart();
        try
        {
            if (CreateRestorePoint && RestorePointPossible)
            {
                CurrentStep = "Point de restauration";
                ProgressDetail = "Création d'un point de restauration Windows (peut prendre une minute)…";
                var restore = await Task.Run(() => _engine.RestorePoints.Create($"Déménageur : {ExpectedName}"));
                if (!restore.Created)
                {
                    RestorePointMessage = restore.Message;
                    _restorePointDecision = new TaskCompletionSource<bool>();
                    AskRestorePointDecision = true;
                    var proceed = await _restorePointDecision.Task;
                    AskRestorePointDecision = false;
                    if (!proceed)
                    {
                        Finish(false, "Opération abandonnée", "Rien n'a été modifié.", []);
                        return;
                    }
                }
            }

            var progress = new Progress<OperationProgress>(OnProgress);
            OperationResult result;
            if (IsMove)
            {
                result = await Task.Run(() => _engine.ExecuteMove(_plan!, p => ((IProgress<OperationProgress>)progress).Report(p), _cancellation.Token));
            }
            else
            {
                result = await Task.Run(() => _engine.ExecuteUndo(_undoPlan!, p => ((IProgress<OperationProgress>)progress).Report(p), _cancellation.Token));
            }

            Changed = true;
            var title = result.Outcome switch
            {
                OperationOutcome.Completed => IsMove ? "Déplacement terminé et vérifié" : "Annulation terminée",
                OperationOutcome.CompletedWithPendingCleanup => IsMove ? "Déplacement terminé (nettoyage en attente)" : "Annulation terminée (nettoyage en attente)",
                OperationOutcome.RolledBack => "Opération annulée : tout est revenu à l'état initial",
                _ => "Intervention nécessaire",
            };
            Finish(result.Succeeded, title, result.Message, result.Warnings);
        }
        catch (Exception ex)
        {
            Finish(false, "Erreur", ex.Message, []);
        }
        finally
        {
            _engine.OperationInProgress = false;
            CanCancel = false;
        }
    }

    private void OnProgress(OperationProgress progress)
    {
        if (progress.Step != CurrentStep)
        {
            CurrentStep = progress.Step;
            var found = false;
            foreach (var line in Steps)
            {
                if (line.Title == progress.Step)
                {
                    line.IsCurrent = true;
                    line.State = "en cours";
                    found = true;
                }
                else if (!found)
                {
                    line.IsCurrent = false;
                    line.IsDone = true;
                    line.State = "terminé";
                }
            }

            // Après le point de non-retour, on ne peut plus annuler.
            if (progress.Step == MoveSteps.DeleteOriginal || progress.Step.StartsWith("Suppression", StringComparison.Ordinal))
            {
                CanCancel = false;
            }
        }

        if (progress.Transfer is { } transfer && transfer.BytesTotal > 0)
        {
            Percent = Math.Min(100, 100.0 * transfer.BytesDone / transfer.BytesTotal);
            var speed = _clock.Elapsed.TotalSeconds > 1 ? transfer.BytesDone / _clock.Elapsed.TotalSeconds : 0;
            ProgressDetail = $"{MovePlanner.Bytes(transfer.BytesDone)} / {MovePlanner.Bytes(transfer.BytesTotal)}"
                             + (speed > 0 ? $" · {MovePlanner.Bytes((long)speed)}/s" : string.Empty)
                             + $" · {transfer.ItemsDone}/{transfer.ItemsTotal} éléments"
                             + (transfer.CurrentItem is { } item ? $"\n{item}" : string.Empty);
        }
        else
        {
            ProgressDetail = string.Empty;
        }
    }

    private void Finish(bool success, string title, string message, IEnumerable<string> warnings)
    {
        ResultSuccess = success;
        ResultTitle = title;
        ResultMessage = message;
        foreach (var warning in warnings)
        {
            Warnings.Add(warning);
        }

        foreach (var line in Steps.Where(_ => success))
        {
            line.IsDone = true;
            line.IsCurrent = false;
            line.State = "terminé";
        }

        Step = WizardStep.Done;
    }

    [RelayCommand]
    private void Cancel()
    {
        if (Step == WizardStep.Running)
        {
            if (CanCancel)
            {
                ProgressDetail = "Annulation demandée : retour à l'état initial…";
                _cancellation?.Cancel();
            }

            return;
        }

        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();

    private static string Duration(TimeSpan duration)
        => duration.TotalMinutes < 1 ? "moins d'une minute" : $"environ {Math.Ceiling(duration.TotalMinutes)} min";
}
