using System.IO;
using System.Runtime.InteropServices;

namespace SetupBootstrapper;

/// <summary>
/// Injects an icon into a NATIVE (overlay-free) PE via the Win32 resource-update APIs.
///
/// This is ONLY safe for the AOT-compiled per-app uninstaller, which is a normal native PE with
/// no appended data. It must NEVER be used on the single-file .NET bootstrapper / setup EXE:
/// EndUpdateResource rewrites the PE and discards any appended overlay, which would strip the
/// .NET single-file bundle (and the appended ZIP). See IconExtractor for the why.
/// </summary>
internal static class NativeIconInjector
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr BeginUpdateResourceW(string pFileName, bool bDeleteExistingResources);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool UpdateResourceW(IntPtr hUpdate, IntPtr lpType, IntPtr lpName, ushort wLanguage, byte[] lpData, uint cbData);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool EndUpdateResourceW(IntPtr hUpdate, bool fDiscard);

    private static readonly IntPtr RT_ICON = 3;
    private static readonly IntPtr RT_GROUP_ICON = 14;

    /// <summary>Extracts the icon associated with an EXE and injects it into the target native PE.</summary>
    public static bool InjectFromExe(string sourceExePath, string targetPePath)
    {
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(sourceExePath);
            if (icon is null) return false;

            var tempIco = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".ico");
            try
            {
                using (var fs = File.Create(tempIco))
                    icon.Save(fs);
                return InjectFromIco(tempIco, targetPePath);
            }
            finally
            {
                try { File.Delete(tempIco); } catch { }
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Injects a .ico file's images as the primary icon (group id 1) of the target native PE.</summary>
    public static bool InjectFromIco(string icoPath, string targetPePath)
    {
        byte[] icoBytes;
        try { icoBytes = File.ReadAllBytes(icoPath); }
        catch { return false; }

        if (icoBytes.Length < 6)
            return false;

        var count = BitConverter.ToUInt16(icoBytes, 4);
        if (count == 0 || count > 255)
            return false;

        const int entrySize = 16;
        const int headerSize = 6;
        var dirSize = headerSize + count * 14;
        var dirBytes = new byte[dirSize];

        Array.Copy(icoBytes, 0, dirBytes, 0, headerSize);

        var iconImages = new List<byte[]>();

        for (var i = 0; i < count; i++)
        {
            var entryStart = headerSize + i * entrySize;
            var offset = BitConverter.ToUInt32(icoBytes, entryStart + 12);
            var size = BitConverter.ToUInt32(icoBytes, entryStart + 8);

            if (offset + size > icoBytes.Length)
                continue;

            var imageData = new byte[size];
            Array.Copy(icoBytes, offset, imageData, 0, size);

            var id = (ushort)(i + 1);
            iconImages.Add(imageData);

            // GRPICONDIRENTRY (14 bytes): first 12 bytes from the ICO entry, then the 2-byte id.
            var dirEntryStart = headerSize + i * 14;
            Array.Copy(icoBytes, entryStart, dirBytes, dirEntryStart, 12);
            dirBytes[dirEntryStart + 12] = (byte)(id & 0xFF);
            dirBytes[dirEntryStart + 13] = (byte)((id >> 8) & 0xFF);
        }

        var hUpdate = BeginUpdateResourceW(targetPePath, false);
        if (hUpdate == IntPtr.Zero)
            return false;

        try
        {
            UpdateResourceW(hUpdate, RT_GROUP_ICON, 1, 0, dirBytes, (uint)dirSize);
            for (var i = 0; i < iconImages.Count; i++)
                UpdateResourceW(hUpdate, RT_ICON, (IntPtr)(i + 1), 0, iconImages[i], (uint)iconImages[i].Length);
        }
        finally
        {
            EndUpdateResourceW(hUpdate, false);
        }

        return true;
    }
}
