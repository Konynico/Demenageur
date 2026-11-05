using System.Security.AccessControl;
using System.Security.Principal;

namespace Demenageur.Core.Copy;

/// <summary>Manipulation des descripteurs de sécurité (format binaire auto-relatif).</summary>
public static class SecurityDescriptorTools
{
    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier AdministratorsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    /// <summary>
    /// Transforme un descripteur en version « explicite et protégée » : les ACE héritées deviennent explicites
    /// et l'héritage du parent est coupé. Utilisé pour la racine de destination, qui doit garder exactement
    /// les droits effectifs de la source alors que son nouveau parent est différent.
    /// </summary>
    public static byte[] ToProtectedExplicit(byte[] securityDescriptor)
    {
        var sd = new RawSecurityDescriptor(securityDescriptor, 0);
        if (sd.DiscretionaryAcl is { } dacl)
        {
            for (var i = 0; i < dacl.Count; i++)
            {
                dacl[i].AceFlags &= ~AceFlags.Inherited;
            }
        }

        sd.SetFlags((sd.ControlFlags | ControlFlags.DiscretionaryAclProtected) & ~ControlFlags.SelfRelative);
        var result = new byte[sd.BinaryLength];
        sd.GetBinaryForm(result, 0);
        return result;
    }

    /// <summary>
    /// Descripteur « privé » posé sur la destination pendant la copie et la vérification :
    /// SYSTEM, Administrateurs et l'utilisateur courant uniquement, héritage coupé.
    /// Personne d'autre ne peut lire ni modifier les fichiers tant qu'ils ne sont pas vérifiés.
    /// </summary>
    public static byte[] CreatePrivateDirectoryDescriptor()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException("Utilisateur courant introuvable.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { SystemSid, AdministratorsSid, user }.Distinct())
        {
            security.AddAccessRule(new FileSystemAccessRule(
                sid,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        return security.GetSecurityDescriptorBinaryForm();
    }

    /// <summary>Forme SDDL (propriétaire, groupe, DACL) pour comparer deux descripteurs.</summary>
    public static string ToSddl(byte[] securityDescriptor, AccessControlSections sections = AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access)
        => new RawSecurityDescriptor(securityDescriptor, 0).GetSddlForm(sections);

    /// <summary>
    /// Vrai si les deux descripteurs accordent exactement les mêmes droits (mêmes ACE dans le même ordre,
    /// sans tenir compte du drapeau « hérité »).
    /// </summary>
    public static bool EffectiveDaclEquals(byte[] a, byte[] b)
    {
        var aceA = ExplicitAces(a);
        var aceB = ExplicitAces(b);
        return aceA.Count == aceB.Count && aceA.Zip(aceB).All(p => p.First.AsSpan().SequenceEqual(p.Second));
    }

    private static List<byte[]> ExplicitAces(byte[] securityDescriptor)
    {
        var result = new List<byte[]>();
        var dacl = new RawSecurityDescriptor(securityDescriptor, 0).DiscretionaryAcl;
        if (dacl is null)
        {
            return result;
        }

        for (var i = 0; i < dacl.Count; i++)
        {
            var ace = dacl[i];
            ace.AceFlags &= ~AceFlags.Inherited;
            var bytes = new byte[ace.BinaryLength];
            ace.GetBinaryForm(bytes, 0);
            result.Add(bytes);
        }

        return result;
    }

    /// <summary>
    /// Vrai si les deux DACL sont strictement identiques (mêmes ACE, mêmes drapeaux « hérité », même protection).
    /// Le drapeau de contrôle « auto-héritée » (AI) est ignoré : l'API bas niveau qui pose le descripteur sans
    /// recalculer l'héritage ne le conserve pas, et il est sans effet sur les droits accordés.
    /// </summary>
    public static bool DaclEquals(byte[] a, byte[] b)
    {
        var sdA = new RawSecurityDescriptor(a, 0);
        var sdB = new RawSecurityDescriptor(b, 0);
        if (((sdA.ControlFlags ^ sdB.ControlFlags) & ControlFlags.DiscretionaryAclProtected) != 0)
        {
            return false;
        }

        var daclA = sdA.DiscretionaryAcl;
        var daclB = sdB.DiscretionaryAcl;
        if (daclA is null || daclB is null)
        {
            return daclA is null && daclB is null;
        }

        var bytesA = new byte[daclA.BinaryLength];
        var bytesB = new byte[daclB.BinaryLength];
        daclA.GetBinaryForm(bytesA, 0);
        daclB.GetBinaryForm(bytesB, 0);
        return bytesA.AsSpan().SequenceEqual(bytesB);
    }

    public static bool IsDaclProtected(byte[] securityDescriptor)
        => (new RawSecurityDescriptor(securityDescriptor, 0).ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0;

    /// <summary>Propriétaire du descripteur.</summary>
    public static SecurityIdentifier? GetOwner(byte[] securityDescriptor)
        => new RawSecurityDescriptor(securityDescriptor, 0).Owner;
}
