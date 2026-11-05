using System.Diagnostics;
using System.IO.Hashing;
using System.Text;
using Demenageur.Core.FileSystem;
using Demenageur.Windows.FileSystem;

namespace Demenageur.Tests.Infrastructure;

/// <summary>
/// Bac à sable de test : un dossier unique sous <c>%TEMP%\DemenageurTests</c>, un dossier « canari »
/// (visé par des jonctions de test, et qui ne doit JAMAIS changer) et une garde de périmètre limitée au bac à sable.
/// À la fin du test, le canari est vérifié puis le bac à sable est supprimé.
/// </summary>
public sealed class TestSandbox : IDisposable
{
    public static readonly string BaseDirectory = Path.Combine(Path.GetTempPath(), "DemenageurTests");

    private readonly string _canaryHash;

    public TestSandbox()
    {
        Root = Path.Combine(BaseDirectory, Guid.NewGuid().ToString("N")[..12]);
        AssertInsideTestBase(Root);
        Directory.CreateDirectory(Root);
        Canary = Path.Combine(Root, "_canari");
        Directory.CreateDirectory(Path.Combine(Canary, "sous-dossier"));
        File.WriteAllText(Path.Combine(Canary, "precieux.txt"), "Ne jamais toucher à ce fichier.");
        File.WriteAllBytes(Path.Combine(Canary, "sous-dossier", "donnees.bin"), Enumerable.Range(0, 5000).Select(i => (byte)i).ToArray());
        _canaryHash = HashTree(Canary);
        RawFs = new Win32FileSystem();
    }

    /// <summary>Racine du bac à sable.</summary>
    public string Root { get; }

    /// <summary>Dossier qui ne doit jamais être modifié.</summary>
    public string Canary { get; }

    /// <summary>Système de fichiers réel, SANS garde (à n'utiliser que pour préparer ou inspecter).</summary>
    public Win32FileSystem RawFs { get; }

    public string PathOf(params string[] parts) => Path.Combine([Root, .. parts]);

    /// <summary>
    /// Système de fichiers gardé : lectures et écritures limitées au bac à sable, écritures limitées aux racines données,
    /// canari et dossiers système intouchables.
    /// </summary>
    public PathGuard Guarded(params string[] writableRoots)
        => new(RawFs, PathScope.Create(
            RawFs,
            writableRoots,
            protectedExact: [Root],
            protectedSubtrees: [Canary, Environment.GetFolderPath(Environment.SpecialFolder.Windows)],
            sandboxRoot: Root));

    public bool CanaryIntact() => string.Equals(HashTree(Canary), _canaryHash, StringComparison.Ordinal);

    public void Dispose()
    {
        var intact = Directory.Exists(Canary) && CanaryIntact();

        // Un processus tué par un test peut libérer son dossier courant avec un léger retard : on réessaie.
        for (var attempt = 0; attempt < 15 && Directory.Exists(Root); attempt++)
        {
            DeleteSandboxTree(Root);
            if (Directory.Exists(Root))
            {
                Thread.Sleep(200);
            }
        }
        if (!intact)
        {
            throw new InvalidOperationException("Le dossier canari a été modifié pendant le test : violation du périmètre !");
        }
    }

    public static void AssertInsideTestBase(string path)
    {
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(BaseDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Chemin de test hors de {BaseDirectory} : {full}");
        }
    }

    /// <summary>Empreinte d'une arborescence : noms, attributs utiles et contenus (sans suivre les liens).</summary>
    public static string HashTree(string root)
    {
        var hasher = new XxHash128();
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false }).OrderBy(e => e.Name, StringComparer.Ordinal))
            {
                hasher.Append(Encoding.UTF8.GetBytes(Path.GetRelativePath(root, entry.FullName) + "|" + (entry.Attributes & ~FileAttributes.Archive)));
                if (entry is FileInfo file && (file.Attributes & FileAttributes.ReparsePoint) == 0)
                {
                    hasher.Append(File.ReadAllBytes(file.FullName));
                }
                else if (entry is DirectoryInfo sub && (sub.Attributes & FileAttributes.ReparsePoint) == 0)
                {
                    stack.Push(sub.FullName);
                }
            }
        }

        return Convert.ToHexString(hasher.GetCurrentHash());
    }

    /// <summary>
    /// Empreinte des seuls noms relatifs et contenus (fichiers ordinaires), calculée par .NET indépendamment
    /// du code testé : sert de contre-vérification d'une copie.
    /// </summary>
    public static string HashTreeContentOnly(string root)
    {
        var hasher = new XxHash128();
        var options = new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false, RecurseSubdirectories = true };
        foreach (var file in Directory.EnumerateFiles(@"\\?\" + root, "*", options).Order(StringComparer.Ordinal))
        {
            hasher.Append(Encoding.Unicode.GetBytes(file[(root.Length + 4)..]));
            hasher.Append(File.ReadAllBytes(file));
        }

        return Convert.ToHexString(hasher.GetCurrentHash());
    }

    /// <summary>Supprime un dossier de test situé sur un autre disque (son chemin doit contenir « \DemenageurTests\ »).</summary>
    public static void DeleteForeignTestTree(string root)
    {
        if (!root.Contains(@"\DemenageurTests\", StringComparison.OrdinalIgnoreCase) || !Directory.Exists(root))
        {
            return;
        }

        DeleteRecursive(@"\\?\" + root);
    }

    /// <summary>Supprime le bac à sable sans jamais suivre une jonction (un lien est supprimé, pas sa cible).</summary>
    public static void DeleteSandboxTree(string root)
    {
        AssertInsideTestBase(root);
        if (!Directory.Exists(root))
        {
            return;
        }

        var extendedRoot = @"\\?\" + root;
        DeleteRecursive(extendedRoot);
    }

    private static void DeleteRecursive(string directory)
    {
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true, RecurseSubdirectories = false }))
        {
            try
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    if (entry is DirectoryInfo)
                    {
                        Directory.Delete(entry.FullName, recursive: false); // supprime le lien seul
                    }
                    else
                    {
                        File.Delete(entry.FullName);
                    }
                }
                else if (entry is DirectoryInfo sub)
                {
                    DeleteRecursive(sub.FullName);
                }
                else
                {
                    entry.Attributes = FileAttributes.Normal;
                    entry.Delete();
                }
            }
            catch (IOException ex)
            {
                Debug.WriteLine($"Nettoyage du bac à sable : {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                Debug.WriteLine($"Nettoyage du bac à sable : {ex.Message}");
            }
        }

        try
        {
            new DirectoryInfo(directory).Attributes = FileAttributes.Directory;
            Directory.Delete(directory, recursive: false);
        }
        catch (IOException ex)
        {
            Debug.WriteLine($"Nettoyage du bac à sable : {ex.Message}");
        }
    }
}
