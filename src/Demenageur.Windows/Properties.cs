using System.Runtime.InteropServices;

// Toutes les DLL natives (kernel32, advapi32, rstrtmgr, srclient, userenv, user32) sont chargées depuis System32 uniquement :
// jamais depuis le dossier de l'application ni le dossier courant, même si l'app était lancée d'un dossier modifiable.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
