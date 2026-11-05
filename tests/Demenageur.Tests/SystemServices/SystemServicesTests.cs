using Demenageur.Core.Abstractions;
using Demenageur.Tests.Infrastructure;
using Demenageur.Windows.Safety;
using Demenageur.Windows.SystemServices;
using Microsoft.Win32;

namespace Demenageur.Tests.SystemServices;

public sealed class SystemServicesTests
{
    [Fact]
    public void Restore_point_availability_matches_the_registry_and_creation_never_forces_protection()
    {
        var service = new RestorePointService();
        var availability = service.GetAvailability();
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var restore = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore");
        if (restore?.GetValue("RPSessionInterval") is 0)
        {
            Assert.Equal(RestorePointAvailability.ProtectionDisabled, availability);

            // Protection désactivée : la création refuse proprement, sans rien activer ni modifier.
            var result = service.Create("Test Déménageur");
            Assert.False(result.Created);
            Assert.Contains("désactivée", result.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Only_one_instance_can_run()
    {
        using var first = SingleInstance.Acquire();
        var secondIsFirst = true;
        var thread = new Thread(() =>
        {
            using var second = SingleInstance.Acquire();
            secondIsFirst = second.IsFirstInstance;
        });
        thread.Start();
        thread.Join();
        Assert.True(first.IsFirstInstance);
        Assert.False(secondIsFirst);
    }

    [Fact]
    public void Last_moment_check_sees_an_open_file_and_critical_section_is_harmless_without_options()
    {
        using var sandbox = new TestSandbox();
        var folder = sandbox.PathOf("logiciel");
        Directory.CreateDirectory(folder);
        var dll = Path.Combine(folder, "module.dll");
        File.WriteAllText(dll, "x");
        var hooks = new WindowsMoveHooks(new CriticalSectionOptions { ExecutableFiles = [dll] }, new RestartManagerProcessInspector(), sandbox.Root);

        using (new FileStream(dll, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.NotNull(hooks.CheckBeforeLock(folder));
        }

        Assert.Null(hooks.CheckBeforeLock(folder));
        hooks.EnterCriticalSection();
        hooks.LeaveCriticalSection();
        WindowsMoveHooks.RecoverExplorer(sandbox.Root); // aucun témoin : rien à faire
        Assert.False(File.Exists(Path.Combine(sandbox.Root, WindowsMoveHooks.ExplorerFlagFileName)));
    }

    [Fact]
    public void Power_status_can_be_read()
    {
        _ = new WindowsPowerStatus().IsOnBattery(out var percent);
        Assert.InRange(percent, -1, 100);
    }
}
