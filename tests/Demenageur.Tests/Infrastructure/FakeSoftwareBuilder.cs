namespace Demenageur.Tests.Infrastructure;

/// <summary>Ce que contient le faux logiciel généré.</summary>
public sealed record FakeSoftware(string Root, int Files, int Directories, int Links)
{
    public const string HiddenFile = @"config\cache.dat";
    public const string SystemFile = @"config\system.cfg";
    public const string ReadOnlyFile = @"bin\readonly-licence.txt";
    public const string AdsFile = @"setup.exe";
    public const string AdsName = "Zone.Identifier";
    public const string HardLinkA = @"bin\tool.exe";
    public const string HardLinkB = @"bin\tool-alias.exe";
    public const string InternalJunction = "current";
    public const string ExternalJunction = "lien_vers_canari";
    public const string LongPathFile = "LONG";
}

/// <summary>
/// Génère un FAUX logiciel (aléatoire à graine fixe) dans le bac à sable, avec tous les cas piégeux :
/// fichiers cachés/système/lecture seule, noms Unicode et piégés, chemins très longs, flux ADS,
/// liens physiques, jonction interne, jonction externe vers le canari.
/// </summary>
public sealed class FakeSoftwareBuilder
{
    private readonly Random _random;
    private readonly string _root;
    private int _files;
    private int _directories;
    private int _links;

    public FakeSoftwareBuilder(string root, int seed = 42)
    {
        TestSandbox.AssertInsideTestBase(root);
        _root = root;
        _random = new Random(seed);
    }

    public bool IncludeTrickyNames { get; init; } = true;

    public bool IncludeLongPath { get; init; } = true;

    public bool IncludeHardLinks { get; init; } = true;

    public bool IncludeJunctions { get; init; } = true;

    /// <summary>Dossier visé par la jonction externe (en général le canari du bac à sable).</summary>
    public string? ExternalJunctionTarget { get; init; }

    public long LargeFileSize { get; init; } = 3 * 1024 * 1024 + 123;

    public FakeSoftware Build()
    {
        Dir(string.Empty);
        Dir("bin");
        Dir(@"bin\plugins");
        Dir("config");
        Dir("data");
        Dir(@"data\niveaux");
        Dir("vide");

        Bytes(@"bin\app.exe", 200_000);
        Bytes(@"bin\core.dll", 1_048_576);
        Bytes(@"bin\plugins\a.dll", 50_000);
        Bytes(@"bin\plugins\b.dll", 4096);
        Bytes(@"data\gros.pak", LargeFileSize);
        Bytes(@"data\niveaux\0-octet.lvl", 0);
        Bytes(@"data\niveaux\4095.lvl", 4095);
        Bytes(@"data\niveaux\4096.lvl", 4096);
        Bytes(@"data\niveaux\4097.lvl", 4097);
        Text(@"config\settings.json", "{ \"langue\": \"fr\" }");

        Text(FakeSoftware.HiddenFile, "cache");
        Attr(FakeSoftware.HiddenFile, FileAttributes.Hidden);
        Text(FakeSoftware.SystemFile, "système");
        Attr(FakeSoftware.SystemFile, FileAttributes.System | FileAttributes.Hidden);
        Text(FakeSoftware.ReadOnlyFile, "licence");
        Attr(FakeSoftware.ReadOnlyFile, FileAttributes.ReadOnly);

        Bytes(FakeSoftware.AdsFile, 10_000);
        File.WriteAllText(Ext(FakeSoftware.AdsFile) + ":" + FakeSoftware.AdsName, "[ZoneTransfer]\r\nZoneId=3\r\n");

        if (IncludeTrickyNames)
        {
            Dir("noms");
            Text(@"noms\données été 日本語.txt", "unicode");
            Text(@"noms\avec espaces .txt", "espaces");
            Text(@"noms\a.", "point final");
            Text(@"noms\b ", "espace finale");
            Text(@"noms\CON", "nom réservé");
            Text(@"noms\é.txt", "NFC");          // U+00E9
            Text("noms\\e\u0301.txt", "NFD");    // e + accent combinant : nom distinct pour NTFS
            Text("noms\\iso\uD800le.txt", "surrogate isolé");
        }

        if (IncludeLongPath)
        {
            var path = FakeSoftware.LongPathFile;
            Dir(path);
            for (var i = 0; i < 6; i++)
            {
                path += $"\\niveau-{i}-{new string('x', 45)}";
                Dir(path);
            }

            Text(path + "\\fichier-au-bout-du-chemin.txt", "chemin de plus de 260 caractères");
        }

        if (IncludeHardLinks)
        {
            Bytes(FakeSoftware.HardLinkA, 30_000);
            TestInterop.CreateHardLink(Full(FakeSoftware.HardLinkB), Full(FakeSoftware.HardLinkA));
            _files++;
        }

        if (IncludeJunctions)
        {
            TestInterop.MklinkJunction(Full(FakeSoftware.InternalJunction), Full("bin"));
            _links++;
            if (ExternalJunctionTarget is not null)
            {
                TestInterop.MklinkJunction(Full(FakeSoftware.ExternalJunction), ExternalJunctionTarget);
                _links++;
            }
        }

        return new FakeSoftware(_root, _files, _directories, _links);
    }

    private string Full(string relative) => relative.Length == 0 ? _root : Path.Combine(_root, relative);

    private string Ext(string relative) => @"\\?\" + Full(relative);

    private void Dir(string relative)
    {
        Directory.CreateDirectory(Ext(relative));
        _directories++;
    }

    private void Bytes(string relative, long size)
    {
        var data = new byte[size];
        _random.NextBytes(data);
        File.WriteAllBytes(Ext(relative), data);
        _files++;
    }

    private void Text(string relative, string content)
    {
        File.WriteAllText(Ext(relative), content);
        _files++;
    }

    private void Attr(string relative, FileAttributes attributes)
        => File.SetAttributes(Ext(relative), attributes);
}
