using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace StickerShell;

// --- minimal shell interop (vtable order matters; do not reorder) ---

[ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellItem
{
    void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
    void GetParent(out IShellItem ppsi);
    void GetDisplayName(uint sigdnName, out IntPtr ppszName);
    void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
    void Compare(IShellItem psi, uint hint, out int piOrder);
}

[ComImport, Guid("b63ea76d-1f85-456f-a19c-48159efa858b"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellItemArray
{
    void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppvOut);
    void GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);
    void GetPropertyDescriptionList(IntPtr keyType, ref Guid riid, out IntPtr ppv);
    void GetAttributes(int attribFlags, int sfgaoMask, out int psfgaoAttribs);
    void GetCount(out uint pdwNumItems);
    void GetItemAt(uint dwIndex, out IShellItem ppsi);
    void EnumItems(out IntPtr ppenumShellItems);
}

[ComImport, Guid("a08ce4d0-fa25-44ab-b57c-c7b1c323e0b9"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IExplorerCommand
{
    void GetTitle(IShellItemArray? psiItemArray, [MarshalAs(UnmanagedType.LPWStr)] out string? ppszName);
    void GetIcon(IShellItemArray? psiItemArray, [MarshalAs(UnmanagedType.LPWStr)] out string? ppszIcon);
    void GetToolTip(IShellItemArray? psiItemArray, [MarshalAs(UnmanagedType.LPWStr)] out string? ppszInfotip);
    void GetCanonicalName(out Guid pguidCommandName);
    void GetState(IShellItemArray? psiItemArray, [MarshalAs(UnmanagedType.Bool)] bool fOkToBeSlow, out uint pCmdState);
    void Invoke(IShellItemArray? psiItemArray, IntPtr pbc);
    void GetFlags(out uint pFlags);
    void EnumSubCommands(out IntPtr ppEnum);
}

// --- the command Explorer activates for the top-level "Open as sticker" entry ---

[ComVisible(true)]
[Guid(Clsid)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class OpenAsStickerCommand : IExplorerCommand
{
    public const string Clsid = "7ad4e2c3-9b5a-4f2e-8c1d-3e5a0b6f4d21";

    private const uint SIGDN_FILESYSPATH = 0x80058000;
    private const uint ECS_ENABLED = 0;
    private const uint ECF_DEFAULT = 0;

    /// <summary>
    /// Folder this assembly was loaded from. AppContext.BaseDirectory is empty
    /// when comhost loads us inside the COM surrogate, so resolve via the
    /// assembly's own location.
    /// </summary>
    private static string PackageDir =>
        Path.GetDirectoryName(typeof(OpenAsStickerCommand).Assembly.Location)
        is { Length: > 0 } dir ? dir : AppContext.BaseDirectory;

    public void GetTitle(IShellItemArray? items, out string? name) =>
        name = "Open as sticker";

    public void GetIcon(IShellItemArray? items, out string? icon) =>
        icon = Path.Combine(PackageDir, "app.ico");

    public void GetToolTip(IShellItemArray? items, out string? tip) =>
        throw new NotImplementedException();   // marshals to E_NOTIMPL; Explorer ignores

    public void GetCanonicalName(out Guid guid) => guid = new Guid(Clsid);

    public void GetState(IShellItemArray? items, bool okToBeSlow, out uint state) =>
        state = ECS_ENABLED;

    public void GetFlags(out uint flags) => flags = ECF_DEFAULT;

    public void EnumSubCommands(out IntPtr ppEnum) =>
        throw new NotImplementedException();

    public void Invoke(IShellItemArray? items, IntPtr pbc)
    {
        try
        {
            Log("Invoke called");
            if (items is null)
            {
                Log("items null");
                return;
            }
            items.GetCount(out uint count);
            Log($"count={count}");
            var args = new StringBuilder();
            for (uint i = 0; i < count; i++)
            {
                items.GetItemAt(i, out var item);
                item.GetDisplayName(SIGDN_FILESYSPATH, out IntPtr pName);
                string? path = Marshal.PtrToStringUni(pName);
                Marshal.FreeCoTaskMem(pName);
                if (!string.IsNullOrEmpty(path))
                    args.Append('"').Append(path).Append("\" ");
            }
            if (args.Length == 0)
            {
                Log("no file-system paths resolved");
                return;
            }

            string exe = Path.Combine(PackageDir, "Sticker.exe");
            Log($"launching: {exe} {args}");
            // UseShellExecute=true: launches via the shell rather than as a
            // direct child of the COM surrogate, escaping its process context.
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args.ToString().TrimEnd(),
                WorkingDirectory = PackageDir,
                UseShellExecute = true,
            });
            Log($"started pid={proc?.Id}");
        }
        catch (Exception ex)
        {
            Log($"EXCEPTION: {ex}");
            // Never let an exception escape into Explorer.
        }
    }

    /// <summary>
    /// Opt-in diagnostics: set STICKER_SHELL_LOG=1 (user env var, then restart
    /// Explorer) to log activations to %TEMP%\sticker-shell.log.
    /// </summary>
    private static readonly bool LogEnabled =
        Environment.GetEnvironmentVariable("STICKER_SHELL_LOG") == "1";

    private static void Log(string message)
    {
        if (!LogEnabled)
            return;
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "sticker-shell.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {message}\r\n");
        }
        catch
        {
            // Logging must never break the handler.
        }
    }
}
