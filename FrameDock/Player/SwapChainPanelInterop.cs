using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;

namespace FrameDock.Player;

internal static class SwapChainPanelInterop
{
    private static readonly Guid InterfaceId = new("63AAD0B8-7C24-40FF-85A8-640D944CC325");

    public static void Attach(SwapChainPanel panel, IntPtr swapChain)
    {
        ArgumentNullException.ThrowIfNull(panel);
        var panelUnknown = Marshal.GetIUnknownForObject(panel);
        IntPtr panelNative = IntPtr.Zero;
        try
        {
            var iid = InterfaceId;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(panelUnknown, ref iid, out panelNative));
            var native = (ISwapChainPanelNative)Marshal.GetObjectForIUnknown(panelNative);
            Marshal.ThrowExceptionForHR(native.SetSwapChain(swapChain));
        }
        finally
        {
            if (panelNative != IntPtr.Zero)
            {
                Marshal.Release(panelNative);
            }

            Marshal.Release(panelUnknown);
        }
    }

    [ComImport]
    [Guid("63AAD0B8-7C24-40FF-85A8-640D944CC325")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISwapChainPanelNative
    {
        [PreserveSig]
        int SetSwapChain(IntPtr swapChain);
    }
}
