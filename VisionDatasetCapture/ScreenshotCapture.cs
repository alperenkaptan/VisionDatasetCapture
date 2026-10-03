using System.Runtime.InteropServices;
using System.Drawing;
using System.Drawing.Imaging;

namespace VisionDatasetCapture
{
    public static class ScreenshotCapture
    {
        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        public static Bitmap? CaptureWindow(IntPtr windowHandle)
        {
            if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle) || IsIconic(windowHandle))
                return null;

            // Visible frame bounds (excludes the invisible resize shadow); fall back to the plain window rect.
            if (DwmGetWindowAttribute(windowHandle, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT rect, Marshal.SizeOf<RECT>()) != 0
                && !GetWindowRect(windowHandle, out rect))
                return null;

            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;

            if (width <= 0 || height <= 0)
                return null;

            // Copy from the composited desktop so GPU/DirectX content is included (PrintWindow returns black for it).
            var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            try
            {
                using var graphics = Graphics.FromImage(bitmap);
                graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
                return bitmap;
            }
            catch
            {
                bitmap.Dispose();
                return null;
            }
        }

        private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);
    }
}
