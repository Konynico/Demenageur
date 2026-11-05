namespace Demenageur.Core.Moving;

/// <summary>
/// Étapes d'un déplacement, dans l'ordre. La simulation les annonce et l'exécution les signale avec
/// exactement les mêmes libellés : un test vérifie que les opérations prévues sont celles exécutées.
/// </summary>
public static class MoveSteps
{
    public const string CreateDestination = "Création de la destination";
    public const string CheckProcesses = "Vérification des processus";
    public const string Lock = "Verrouillage du dossier d'origine";
    public const string Inventory = "Inventaire des fichiers";
    public const string Copy = "Copie";
    public const string VerifyContent = "Vérification du contenu";
    public const string Security = "Permissions";
    public const string CreateJunction = "Création de la jonction";
    public const string ValidateJunction = "Validation de la jonction";
    public const string DeleteOriginal = "Suppression de l'original";

    public static IReadOnlyList<string> All { get; } =
    [
        CreateDestination, CheckProcesses, Lock, Inventory, Copy, VerifyContent, Security, CreateJunction, ValidateJunction, DeleteOriginal,
    ];
}
