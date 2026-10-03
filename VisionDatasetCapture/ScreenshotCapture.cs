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

            if (!GetWindowRect(windowHandle, out var rect))
                return null;

            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;

            if (width <= 0 || height <= 0)
                return null;

            var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            bool ok;
            using (var graphics = Graphics.FromImage(bitmap))
            {
                var hdc = graphics.GetHdc();
                try { ok = PrintWindow(windowHandle, hdc, 2); }
                finally { graphics.ReleaseHdc(hdc); }
            }

            if (!ok)
            {
                bitmap.Dispose();
                return null;
            }

            return bitmap;
        }

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
    }
}
