using System.Runtime.InteropServices;
using System.Windows.Media;

namespace ForgeTekUpdatePackager.Helpers;

internal static class NativeColorPicker
{
    public static Color? PickColor(Color initial, IntPtr ownerHandle)
    {
        var cc = new CHOOSECOLOR
        {
            lStructSize = Marshal.SizeOf<CHOOSECOLOR>(),
            hwndOwner = ownerHandle,
            lpCustColors = Marshal.AllocHGlobal(16 * 4),
            rgbResult = ToWin32Color(initial),
            Flags = CC_RGBINIT | CC_ANYCOLOR,
        };

        try
        {
            return ChooseColor(ref cc)
                ? FromWin32Color(cc.rgbResult)
                : null;
        }
        finally
        {
            Marshal.FreeHGlobal(cc.lpCustColors);
        }
    }

    private const int CC_RGBINIT  = 0x00000001;
    private const int CC_ANYCOLOR = 0x00000100;

    [DllImport("comdlg32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool ChooseColor(ref CHOOSECOLOR lpcc);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct CHOOSECOLOR
    {
        public int    lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public int    rgbResult;
        public IntPtr lpCustColors;
        public int    Flags;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public IntPtr lpTemplateName;
    }

    private static int ToWin32Color(Color c) => c.R | (c.G << 8) | (c.B << 16);
    private static Color FromWin32Color(int rgb) => Color.FromRgb((byte)(rgb & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)((rgb >> 16) & 0xFF));
}
