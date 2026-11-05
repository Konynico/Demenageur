using System.Security.Principal;

[assembly: AssemblyFixture(typeof(Demenageur.Tests.Infrastructure.NotElevatedFixture))]

namespace Demenageur.Tests.Infrastructure;

/// <summary>
/// Première barrière de sécurité des tests : ils refusent de tourner en administrateur.
/// Ainsi, même si un bug échappait à la garde de périmètre, Windows lui-même interdirait
/// toute modification de Program Files ou des autres dossiers protégés.
/// </summary>
public sealed class NotElevatedFixture
{
    public NotElevatedFixture()
    {
        if (IsElevated())
        {
            throw new InvalidOperationException(
                "Les tests de Déménageur refusent de s'exécuter en administrateur. "
                + "Relance-les depuis un terminal NON élevé.");
        }
    }

    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
