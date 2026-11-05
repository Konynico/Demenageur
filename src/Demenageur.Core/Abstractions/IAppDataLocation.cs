namespace Demenageur.Core.Abstractions;

/// <summary>
/// Emplacements des données de l'application (journaux d'opérations, logs).
/// En production : <c>C:\ProgramData\Demenageur</c>, réservé à SYSTEM et aux administrateurs.
/// </summary>
public interface IAppDataLocation
{
    /// <summary>Racine des données de l'application.</summary>
    string DataRoot { get; }

    /// <summary>Dossier des logs techniques (Serilog).</summary>
    string LogsDirectory { get; }

    /// <summary>Dossier des opérations (un sous-dossier par déplacement : journal + manifeste).</summary>
    string OperationsDirectory { get; }
}
