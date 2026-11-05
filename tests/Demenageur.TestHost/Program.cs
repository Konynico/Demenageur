namespace Demenageur.TestHost;

/// <summary>
/// Console utilisée par les tests de crash : exécute une opération sur des dossiers factices
/// et se tue à la N-ième opération disque. Les commandes sont ajoutées à l'étape 3.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        Console.Error.WriteLine("Demenageur.TestHost : aucune commande disponible pour l'instant.");
        return 2;
    }
}
