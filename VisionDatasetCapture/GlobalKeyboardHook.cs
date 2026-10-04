using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;

namespace VisionDatasetCapture
{
    public sealed class GlobalKeyboardHook : IDisposable
    {
        private const int WhKeyboardLl = 13;
        private const int WmKeyDown = 0x0100;
        private const int WmSysKeyDown = 0x0104;
        private const int WmKeyUp = 0x0101;
        private const int WmSysKeyUp = 0x0105;

        private readonly int _targetVirtualKey;
        private readonly Action _onKeyPressed;
        private readonly HashSet<int> _pressedKeys = new();
        private readonly LowLevelKeyboardProc _hookCallback;
        private IntPtr _hookHandle;
        private bool _disposed;

        public GlobalKeyboardHook(Key targetKey, Action onKeyPressed)
        {
            _targetVirtualKey = KeyInterop.VirtualKeyFromKey(targetKey);
            _onKeyPressed = onKeyPressed;
            _hookCallback = HookProcedure;
        }

        public void Start()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(GlobalKeyboardHook));

            if (_hookHandle != IntPtr.Zero)
                return;

            using var process = Process.GetCurrentProcess();
            using var module = process.MainModule;
            var moduleName = module?.ModuleName;
            var moduleHandle = string.IsNullOrWhiteSpace(moduleName)
                ? IntPtr.Zero
                : GetModuleHandle(moduleName);

            _hookHandle = SetWindowsHookEx(WhKeyboardLl, _hookCallback, moduleHandle, 0);
            if (_hookHandle == IntPtr.Zero)
                throw new InvalidOperationException("Failed to start keyboard hook.");
        }

        public void Stop()
        {
            if (_hookHandle == IntPtr.Zero)
                return;

            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
            _pressedKeys.Clear();
        }

        private IntPtr HookProcedure(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                var vkCode = Marshal.ReadInt32(lParam);
                var message = wParam.ToInt32();

                if (vkCode == _targetVirtualKey)
                {
                    if (message == WmKeyDown || message == WmSysKeyDown)
                    {
                        if (_pressedKeys.Add(vkCode))
                            _onKeyPressed();
                    }
                    else if (message == WmKeyUp || message == WmSysKeyUp)
                    {
                        _pressedKeys.Remove(vkCode);
                    }
                }
            }

            return CallNextHookEx(_hookHandle, code, wParam, lParam);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            Stop();
            _disposed = true;
            GC.SuppressFinalize(this);
        }

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);
    }
}
