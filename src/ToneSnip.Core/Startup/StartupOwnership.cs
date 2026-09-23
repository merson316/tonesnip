namespace ToneSnip.Core.Startup;

/// <summary>What arranges for ToneSnip to start with Windows on this machine.</summary>
public enum StartupOwner
{
    /// <summary>This unpackaged copy, through the per-user Run key.</summary>
    PortableRunKey,
    /// <summary>This packaged copy, through its <c>ToneSnipStartup</c> StartupTask.</summary>
    ThisPackage,
    /// <summary>An installed package that is not this process. Its StartupTask is out of reach from here.</summary>
    InstalledPackage,
}

/// <summary>
/// Which startup mechanism a launch may touch. The portable exe and the MSIX share one settings.json, so a single
/// "Start with Windows" once armed both the Run key and the package's StartupTask; two entries then raced at boot, and
/// the launch that lost the single-instance mutex forwarded an empty command line, which opens Settings
/// (<c>Program.Main</c>). A package that arranges startup owns it, and every other copy keeps the Run key clear.
/// </summary>
public static class StartupOwnership
{
    /// <param name="packageClaimsStartup">
    /// True when the installed package's StartupTask is anything but plain <c>Disabled</c>: armed, or vetoed in Windows
    /// Settings, both of which are the package's business rather than a portable copy's.
    /// <para>False when the task has never been touched. The MSIX ships it disabled, so an installed package that has
    /// never armed it is arranging nothing, and a portable copy that deferred to it would clear the Run key and leave
    /// the switch on with nothing starting at boot - which the package cannot correct, since the only thing that would
    /// launch it is the startup it never got.</para>
    /// </param>
    public static StartupOwner For(bool runningPackaged, bool packageInstalled, bool packageClaimsStartup)
        => runningPackaged ? StartupOwner.ThisPackage
         : packageInstalled && packageClaimsStartup ? StartupOwner.InstalledPackage
         : StartupOwner.PortableRunKey;

    /// <summary>True when the Run key is this copy's to write and remove as the setting says; false when the owner's
    /// only business with it is to make sure it is absent.</summary>
    public static bool RunKeyFollowsSetting(StartupOwner owner) => owner == StartupOwner.PortableRunKey;

    /// <summary>
    /// True when the stored setting, rather than anything readable on the machine, answers "does ToneSnip start with
    /// Windows?".
    /// <para>A portable copy cannot read an installed package's StartupTask, and the settings window writes what it
    /// reads back to the shared settings.json, so reporting the deliberately empty Run key as "off" would disable the
    /// installed app's autostart behind its back.</para>
    /// </summary>
    public static bool StoredSettingIsAuthoritative(StartupOwner owner) => owner == StartupOwner.InstalledPackage;
}
