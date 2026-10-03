using System.Diagnostics;

namespace VisionDatasetCapture
{
    public class ProcessInfo
    {
        public int ProcessId { get; set; }
        public IntPtr WindowHandle { get; set; }
        public string DisplayName { get; set; } = "";

        public override string ToString() => DisplayName;
    }

    public static class ProcessSelector
    {
        public static List<ProcessInfo> GetAvailableProcesses()
        {
            var processes = new List<ProcessInfo>();

            try
            {
                foreach (var process in Process.GetProcesses())
                {
                    try
                    {
                        if (process.MainWindowHandle != IntPtr.Zero && !string.IsNullOrWhiteSpace(process.MainWindowTitle))
                        {
                            var displayName = string.IsNullOrWhiteSpace(process.MainWindowTitle)
                                ? process.ProcessName
                                : process.MainWindowTitle;

                            processes.Add(new ProcessInfo
                            {
                                ProcessId = process.Id,
                                WindowHandle = process.MainWindowHandle,
                                DisplayName = displayName
                            });
                        }
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }
            catch
            {
                // Silently handle any process enumeration errors
            }

            return processes.OrderBy(p => p.DisplayName).ToList();
        }

        public static IntPtr GetWindowHandleForProcessId(int processId)
        {
            try
            {
                var process = Process.GetProcessById(processId);
                var handle = process.MainWindowHandle;
                process.Dispose();
                return handle;
            }
            catch
            {
                return IntPtr.Zero;
            }
        }
    }
}
