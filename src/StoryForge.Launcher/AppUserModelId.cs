using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace StoryForge.Launcher;

// Chrome's own AppUserModelID (AUMID) is what Windows uses to decide taskbar grouping and pin identity —
// it is NOT based on the exe path. A plain `chrome.exe --app=URL` window inherits the same generic AUMID
// as regular Chrome, so pinning it to the taskbar merges into (or silently no-ops against) an already-
// pinned Chrome icon instead of creating a distinct StoryForge icon. Fixing this needs two things tagged
// with the SAME custom AUMID: the pinned .lnk shortcut (so Windows knows the pin's identity) and the live
// Chrome window Launcher spawns (so the running instance's taskbar button matches that pin instead of
// falling back to Chrome's own identity). Both require raw COM (IPropertyStore) — there's no managed API
// for either.
internal static class AppUserModelId
{
    private static readonly Guid Iid_IPropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
    private static readonly Guid AppUserModelFmtid = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");

    private static PROPERTYKEY PKEY_AppUserModel_ID => new() { fmtid = AppUserModelFmtid, pid = 5 };
    private static PROPERTYKEY PKEY_AppUserModel_RelaunchCommand => new() { fmtid = AppUserModelFmtid, pid = 2 };
    private static PROPERTYKEY PKEY_AppUserModel_RelaunchIconResource => new() { fmtid = AppUserModelFmtid, pid = 3 };
    private static PROPERTYKEY PKEY_AppUserModel_RelaunchDisplayNameResource => new() { fmtid = AppUserModelFmtid, pid = 4 };

    // Tags an already-running window (here: the Chrome app-mode window Launcher just spawned) with our
    // AUMID so its taskbar button is recognized as StoryForge rather than grouped with regular Chrome.
    // The AUMID alone isn't enough: Chrome's own code already stamped this window with ITS OWN
    // RelaunchCommand (plain `chrome.exe`, no --app=/--user-data-dir), used when Windows relaunches a
    // pinned-from-this-live-window icon. Without overwriting that too, pinning looks like it worked (own
    // icon, no more merging into regular Chrome) but clicking the pin launches bare Chrome — landing on its
    // normal multi-profile picker — instead of going through our launcherExePath.
    public static void SetForWindow(IntPtr hwnd, string aumid, string launcherExePath, string displayName)
    {
        var riid = Iid_IPropertyStore;
        SHGetPropertyStoreForWindow(hwnd, ref riid, out var store);
        try
        {
            var relaunchCommand = $"\"{launcherExePath}\"";
            SetStringValue(store, PKEY_AppUserModel_ID, aumid);
            SetStringValue(store, PKEY_AppUserModel_RelaunchCommand, relaunchCommand);
            SetStringValue(store, PKEY_AppUserModel_RelaunchDisplayNameResource, displayName);
            SetStringValue(store, PKEY_AppUserModel_RelaunchIconResource, $"{launcherExePath},0");
            store.Commit();
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    private static void SetStringValue(IPropertyStore store, PROPERTYKEY key, string value)
    {
        var pv = PropVariant.FromString(value);
        var k = key;
        store.SetValue(ref k, ref pv);
        pv.Dispose();
    }

    // (Re)creates a .lnk (Start Menu and/or Desktop) with the AUMID baked directly into the file, so a
    // manual "Pin to Start"/"Pin to taskbar" on it registers under our identity instead of Chrome's.
    // Re-running this every launch keeps the shortcut pointed at wherever the exe currently lives (survives
    // a rebuild moving/recreating bin/Release) without the user re-pinning after every publish.
    public static void EnsureShortcut(string shortcutPath, string targetExePath, string workingDirectory, string aumid, string displayName)
    {
        // The plain shortcut fields (target/working dir/icon) go through WScript.Shell — the same
        // late-bound COM automation `New-Object -ComObject WScript.Shell` uses, well-proven and side-steps
        // needing a hand-declared IShellLinkW vtable at all. Only the AUMID stamp (which WScript.Shell has
        // no property for) touches raw COM, narrowing the risk surface to just IPropertyStore.
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell not available");
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(shortcutPath);
        shortcut.TargetPath = targetExePath;
        shortcut.WorkingDirectory = workingDirectory;
        shortcut.IconLocation = targetExePath + ",0";
        shortcut.Save();
        Marshal.ReleaseComObject(shortcut);
        Marshal.ReleaseComObject(shell);

        var link = (IPersistFile)new ShellLinkCoClass();
        link.Load(shortcutPath, 0 /* STGM_READ */);

        var store = (IPropertyStore)link;
        var relaunchCommand = $"\"{targetExePath}\"";
        SetStringValue(store, PKEY_AppUserModel_ID, aumid);
        SetStringValue(store, PKEY_AppUserModel_RelaunchCommand, relaunchCommand);
        SetStringValue(store, PKEY_AppUserModel_RelaunchDisplayNameResource, displayName);
        SetStringValue(store, PKEY_AppUserModel_RelaunchIconResource, $"{targetExePath},0");
        store.Commit();

        link.Save(shortcutPath, true);

        Marshal.ReleaseComObject(link);
    }

    [DllImport("shell32.dll", PreserveSig = false)]
    private static extern void SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid riid, out IPropertyStore propertyStore);

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public int pid;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant : IDisposable
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pointerValue;

        public static PropVariant FromString(string value) => new()
        {
            vt = 31, // VT_LPWSTR
            pointerValue = Marshal.StringToCoTaskMemUni(value),
        };

        public void Dispose() => PropVariantClear(ref this);

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PropVariant pvar);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint cProps);
        void GetAt(uint iProp, out PROPERTYKEY pkey);
        void GetValue(ref PROPERTYKEY key, out PropVariant pv);
        void SetValue(ref PROPERTYKEY key, ref PropVariant pv);
        void Commit();
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkCoClass
    {
    }

}
