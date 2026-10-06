using System.Diagnostics;

namespace VisionDatasetCapture
{
    /// <summary>
    /// Captures detailed information about a target process window.
    /// </summary>
    public class ProcessWindowInfo
    {
        /// <summary>
        /// The process ID.
        /// </summary>
        public int ProcessId { get; set; }

        /// <summary>
        /// The process name (e.g., "notepad").
        /// </summary>
        public string ProcessName { get; set; } = "";

        /// <summary>
        /// Window rectangle (left, top, right, bottom) in screen coordinates.
        /// </summary>
        public RECT WindowRect { get; set; }

        /// <summary>
        /// Client area rectangle (relative to window).
        /// </summary>
        public RECT ClientRect { get; set; }

        /// <summary>
        /// Window width in pixels.
        /// </summary>
        public int WindowWidth => WindowRect.Right - WindowRect.Left;

        /// <summary>
        /// Window height in pixels.
        /// </summary>
        public int WindowHeight => WindowRect.Bottom - WindowRect.Top;

        /// <summary>
        /// Window X coordinate (left position).
        /// </summary>
        public int WindowX => WindowRect.Left;

        /// <summary>
        /// Window Y coordinate (top position).
        /// </summary>
        public int WindowY => WindowRect.Top;

        /// <summary>
        /// Client area width in pixels.
        /// </summary>
        public int ClientWidth => ClientRect.Right - ClientRect.Left;

        /// <summary>
        /// Client area height in pixels.
        /// </summary>
        public int ClientHeight => ClientRect.Bottom - ClientRect.Top;

        /// <summary>
        /// Gets a formatted string with all process info.
        /// </summary>
        public override string ToString()
        {
            return $"{ProcessName} (PID: {ProcessId}) | Window: {WindowWidth}x{WindowHeight} @ ({WindowX}, {WindowY}) | Client: {ClientWidth}x{ClientHeight}";
        }
    }

    /// <summary>
    /// Rectangle structure compatible with Win32 RECT.
    /// </summary>
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    /// <summary>
    /// Retrieves detailed process window information using Win32 APIs.
    /// </summary>
    public static class ProcessWindowInfoProvider
    {
        /// <summary>
        /// Gets detailed window information for a window handle.
        /// </summary>
        public static ProcessWindowInfo? GetWindowInfo(IntPtr windowHandle)
        {
            if (windowHandle == IntPtr.Zero)
                return null;

            try
            {
                if (!Win32Interop.GetWindowRect(windowHandle, out RECT windowRect))
                    return null;

                if (!Win32Interop.GetClientRect(windowHandle, out RECT clientRect))
                    return null;

                if (!Win32Interop.GetWindowThreadProcessId(windowHandle, out uint processId))
                    return null;

                Process? process = null;
                string processName = "";

                try
                {
                    process = Process.GetProcessById((int)processId);
                    processName = process.ProcessName;
                }
                catch
                {
                    processName = $"PID_{processId}";
                }

                return new ProcessWindowInfo
                {
                    ProcessId = (int)processId,
                    ProcessName = processName,
                    WindowRect = windowRect,
                    ClientRect = clientRect
                };
            }
            catch
            {
                return null;
            }
        }
    }
}
