using ToneSnip.Core.Startup;
using Xunit;

namespace ToneSnip.Core.Tests;

public class StartupOwnershipTests
{
    [Fact]
    public void A_portable_copy_with_no_package_installed_owns_the_run_key()
    {
        StartupOwner owner = StartupOwnership.For(runningPackaged: false, packageInstalled: false, packageClaimsStartup: false);
        Assert.Equal(StartupOwner.PortableRunKey, owner);
        Assert.True(StartupOwnership.RunKeyFollowsSetting(owner));
        Assert.False(StartupOwnership.StoredSettingIsAuthoritative(owner));
    }

    [Fact]
    public void The_packaged_copy_owns_its_startup_task_and_no_run_key()
    {
        // Both were armed at once: the MSIX's StartupTask and the Run key a portable copy had left behind, so the
        // second boot launch forwarded an empty command line and the running instance opened Settings.
        StartupOwner owner = StartupOwnership.For(runningPackaged: true, packageInstalled: true, packageClaimsStartup: true);
        Assert.Equal(StartupOwner.ThisPackage, owner);
        Assert.False(StartupOwnership.RunKeyFollowsSetting(owner));
    }

    [Fact]
    public void The_packaged_copy_owns_its_startup_task_however_the_task_stands()
    {
        // Its own task is always its business to arm, and it ships disabled: the package enables it on the first
        // launch with the setting on.
        StartupOwner owner = StartupOwnership.For(runningPackaged: true, packageInstalled: true, packageClaimsStartup: false);
        Assert.Equal(StartupOwner.ThisPackage, owner);
    }

    [Fact]
    public void A_portable_copy_beside_a_package_that_claims_startup_leaves_the_run_key_clear()
    {
        StartupOwner owner = StartupOwnership.For(runningPackaged: false, packageInstalled: true, packageClaimsStartup: true);
        Assert.Equal(StartupOwner.InstalledPackage, owner);
        Assert.False(StartupOwnership.RunKeyFollowsSetting(owner));
    }

    [Fact]
    public void A_portable_copy_owns_the_run_key_when_the_installed_package_arranges_no_startup()
    {
        // The MSIX ships its task disabled, so an installed package that has never armed it is arranging nothing. A
        // portable copy that deferred here would clear the Run key and leave the switch on with nothing starting at
        // boot, and the package cannot correct that: the only thing that would launch it is the startup it never got.
        StartupOwner owner = StartupOwnership.For(runningPackaged: false, packageInstalled: true, packageClaimsStartup: false);
        Assert.Equal(StartupOwner.PortableRunKey, owner);
        Assert.True(StartupOwnership.RunKeyFollowsSetting(owner));
        Assert.False(StartupOwnership.StoredSettingIsAuthoritative(owner));
    }

    [Fact]
    public void The_stored_setting_answers_for_a_portable_copy_beside_a_package_that_claims_startup()
    {
        // The settings window reconciles the switch against what Windows reports and writes the answer back to the
        // shared settings.json. A portable copy cannot read the package's StartupTask, so reporting the empty Run key
        // as "off" would turn the installed app's autostart off behind its back.
        StartupOwner owner = StartupOwnership.For(runningPackaged: false, packageInstalled: true, packageClaimsStartup: true);
        Assert.True(StartupOwnership.StoredSettingIsAuthoritative(owner));
    }

    [Fact]
    public void A_packaged_copy_reports_its_own_startup_task_rather_than_the_stored_setting()
    {
        StartupOwner owner = StartupOwnership.For(runningPackaged: true, packageInstalled: true, packageClaimsStartup: true);
        Assert.False(StartupOwnership.StoredSettingIsAuthoritative(owner));
    }
}
