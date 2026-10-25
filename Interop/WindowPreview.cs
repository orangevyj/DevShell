using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DevShell.Interop;

/// <summary>
/// A still picture of another application's window, for the taskbar's hover preview.
///
/// DWM can give a live picture through <c>DwmRegisterThumbnail</c>, but a thumbnail is only
/// composed into an ordinary window, and every flyout on this bar is a layered popup
/// (<c>AllowsTransparency</c>) where DWM draws nothing. So the window is asked to paint itself
/// instead. The call waits for the owning thread to answer, and a hung application must not take
/// the bar with it, so it is made from a worker thread and only the finished bitmap crosses back.
/// </summary>
internal static class WindowPreview
{
    /// <summary>
    /// A frozen bitmap of <paramref name="window"/> scaled to fit the card, or null when the
    /// window has no picture to give - it is minimized, gone, or would not answer.
    /// </summary>
    public static BitmapSource? Capture(IntPtr window, int maxWidth, int maxHeight)
    {
        try
        {
            if (window == IntPtr.Zero
                || !NativeMethods.IsWindow(window)
                || NativeMethods.IsIconic(window)
                || !NativeMethods.GetWindowRect(window, out var rect)
                || rect.Width <= 0
                || rect.Height <= 0)
            {
                return null;
            }

            // A window can be larger than its monitor and a virtual desktop can be larger still.
            var width = Math.Min(rect.Width, 4096);
            var height = Math.Min(rect.Height, 4096);

            var screenDc = NativeMethods.GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero)
            {
                return null;
            }

            var memoryDc = IntPtr.Zero;
            var bitmap = IntPtr.Zero;
            var previous = IntPtr.Zero;

            try
            {
                memoryDc = NativeMethods.CreateCompatibleDC(screenDc);
                bitmap = NativeMethods.CreateCompatibleBitmap(screenDc, width, height);
                if (memoryDc == IntPtr.Zero || bitmap == IntPtr.Zero)
                {
                    return null;
                }

                previous = NativeMethods.SelectObject(memoryDc, bitmap);

                if (!NativeMethods.PrintWindow(window, memoryDc, NativeMethods.PW_RENDERFULLCONTENT))
                {
                    return null;
                }

                // The source copies the pixels, so the GDI bitmap can be released straight after.
                var source = Imaging.CreateBitmapSourceFromHBitmap(
                    bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

                // Some windows (hardware-composited ones, mostly) answer with a flat rectangle;
                // the caller shows the application's icon instead of a black box.
                if (IsFlat(source))
                {
                    return null;
                }

                var scale = Math.Min((double)maxWidth / width, (double)maxHeight / height);
                BitmapSource picture = scale < 1.0
                    ? new TransformedBitmap(source, new ScaleTransform(scale, scale))
                    : source;

                picture.Freeze();
                return picture;
            }
            finally
            {
                if (memoryDc != IntPtr.Zero && previous != IntPtr.Zero)
                {
                    NativeMethods.SelectObject(memoryDc, previous);
                }

                if (bitmap != IntPtr.Zero)
                {
                    NativeMethods.DeleteObject(bitmap);
                }

                if (memoryDc != IntPtr.Zero)
                {
                    NativeMethods.DeleteDC(memoryDc);
                }

                NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }
        catch (Exception ex)
        {
            App.Log("WindowPreview.Capture", ex);
            return null;
        }
    }

    /// <summary>
    /// True when the picture is one colour from corner to corner - the signature of a window that
    /// answered the request without actually drawing anything.
    /// </summary>
    private static bool IsFlat(BitmapSource bitmap)
    {
        var width = bitmap.PixelWidth;
        var height = bitmap.PixelHeight;
        if (width <= 0 || height <= 0)
        {
            return true;
        }

        var stride = width * 4;
        var pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);

        // A few thousand samples are enough to tell a picture from a blank rectangle.
        var step = Math.Max(1, width * height / 2048);
        var colors = new HashSet<int>();

        for (var index = 0; index < width * height; index += step)
        {
            var offset = index / width * stride + index % width * 4;
            colors.Add(pixels[offset] | (pixels[offset + 1] << 8) | (pixels[offset + 2] << 16));

            if (colors.Count > 8)
            {
                return false;
            }
        }

        return true;
    }
}
