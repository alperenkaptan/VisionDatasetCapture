using System.Runtime.InteropServices;

namespace VisionDatasetCapture
{
    public struct POINT
    {
        public int X;
        public int Y;
    }

    /// <summary>
    /// Win32 API interop methods for window and process information retrieval.
    /// </summary>
    public static class Win32Interop
    {
        /// <summary>
        /// Retrieves the dimensions of the bounding rectangle of the specified window.
        /// The dimensions are given in screen coordinates, relative to the upper-left corner of the screen.
        /// </summary>
        [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowRect")]
        private static extern bool GetWindowRectNative(IntPtr hWnd, out RECT lpRect);

        /// <summary>
        /// Retrieves the coordinates of a window's client area.
        /// The client area is the portion of the window where content is displayed.
        /// </summary>
        [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetClientRect")]
        private static extern bool GetClientRectNative(IntPtr hWnd, out RECT lpRect);

        /// <summary>
        /// Retrieves the identifier of the thread and process that created the specified window.
        /// </summary>
        [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowThreadProcessId")]
        private static extern uint GetWindowThreadProcessIdNative(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetCursorPos")]
        private static extern bool GetCursorPosNative(out POINT lpPoint);

        [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetCursorPos")]
        private static extern bool SetCursorPosNative(int x, int y);

        [DllImport("user32.dll", EntryPoint = "GetAsyncKeyState")]
        private static extern short GetAsyncKeyStateNative(int vKey);

        /// <summary>
        /// Retrieves the dimensions of the bounding rectangle of the specified window.
        /// </summary>
        public static bool GetWindowRect(IntPtr windowHandle, out RECT rect)
        {
            return GetWindowRectNative(windowHandle, out rect);
        }

        /// <summary>
        /// Retrieves the coordinates of a window's client area.
        /// </summary>
        public static bool GetClientRect(IntPtr windowHandle, out RECT rect)
        {
            return GetClientRectNative(windowHandle, out rect);
        }

        /// <summary>
        /// Retrieves the process ID that created the specified window.
        /// </summary>
        public static bool GetWindowThreadProcessId(IntPtr windowHandle, out uint processId)
        {
            GetWindowThreadProcessIdNative(windowHandle, out processId);
            return processId != 0;
        }

        public static bool TryGetCursorPos(out POINT point)
        {
            return GetCursorPosNative(out point);
        }

        public static bool SetCursorPos(int x, int y)
        {
            return SetCursorPosNative(x, y);
        }

        public static bool IsLeftMouseDown()
        {
            const int VK_LBUTTON = 0x01;
            return (GetAsyncKeyStateNative(VK_LBUTTON) & 0x8000) != 0;
        }

        public static bool IsEscPressed()
        {
            const int VK_ESCAPE = 0x1B;
            return (GetAsyncKeyStateNative(VK_ESCAPE) & 0x8000) != 0;
        }
    }
}
