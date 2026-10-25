using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace DevShell.Interop;

/// <summary>
/// Optional eye candy: a DWM backdrop behind the bar. Every call is best-effort —
/// if the composition call is unavailable the bar simply keeps its solid background.
/// </summary>
internal static class WindowEffects
{
    /// <summary>Blurs whatever is behind the bar; the bar's own tint is painted on top of it.</summary>
    public static bool TryEnableAcrylic(Window window, Color tint, byte alpha)
        => Apply(window, NativeMethods.AccentState.EnableAcrylicBlurBehind, 0x20 | 0x40 | 0x80, tint, alpha);

    /// <summary>
    /// A translucent backdrop with no blur, for a bar whose own tint is see-through while acrylic is
    /// switched off. The backdrop has to come from somewhere: WPF mixes a translucent brush with black
    /// rather than with what is behind the window, so without this the transparency slider would only
    /// make the bar murky instead of showing the desktop through it.
    /// </summary>
    public static bool TryEnableTransparentBackdrop(Window window, Color tint, byte alpha)
        => Apply(window, NativeMethods.AccentState.TransparentGradient, 0, tint, alpha);

    private static bool Apply(
        Window window, NativeMethods.AccentState state, int flags, Color tint, byte alpha)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
            {
                return false;
            }

            // 0x00BBGGRR is what the composition API expects.
            var gradient = (alpha << 24) | (tint.R << 16) | (tint.G << 8) | tint.B;

            var policy = new NativeMethods.AccentPolicy
            {
                AccentState = state,
                AccentFlags = flags,
                GradientColor = gradient,
            };

            var size = Marshal.SizeOf<NativeMethods.AccentPolicy>();
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(policy, buffer, false);
                var data = new NativeMethods.WindowCompositionAttributeData
                {
                    Attribute = NativeMethods.WCA_ACCENT_POLICY,
                    Data = buffer,
                    SizeOfData = size,
                };

                return NativeMethods.SetWindowCompositionAttribute(handle, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception ex)
        {
            App.Log("Acrylic", ex);
            return false;
        }
    }

    /// <summary>Keeps the bar above the shell's own taskbar, which also claims TOPSET.</summary>
    public static void AssertTopmost(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.SetWindowPos(
            handle,
            NativeMethods.HWND_TOPMOST,
            0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER);
    }

    /// <summary>Keeps a popup or menu - which live in HWNDs of their own - above the bar.</summary>
    public static void AssertTopmost(Visual visual)
    {
        try
        {
            if (PresentationSource.FromVisual(visual) is HwndSource source && source.Handle != IntPtr.Zero)
            {
                NativeMethods.SetWindowPos(
                    source.Handle,
                    NativeMethods.HWND_TOPMOST,
                    0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
                    NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER);
            }
        }
        catch (Exception ex)
        {
            App.Log("Topmost", ex);
        }
    }

    /// <summary>Keeps the bar out of Alt+Tab.</summary>
    public static void HideFromAltTab(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var style = NativeMethods.GetWindowLong(handle, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLong(handle, NativeMethods.GWL_EXSTYLE, style | NativeMethods.WS_EX_TOOLWINDOW);
    }
}
