using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace VisionDatasetCapture
{
    public partial class MainWindow : Window
    {
        private readonly SemaphoreSlim _captureLock = new(1, 1);
        private readonly List<CaptureModeOption> _captureModes = new()
        {
            new CaptureModeOption(CaptureMode.AutoTimed, "Auto timed screenshot"),
            new CaptureModeOption(CaptureMode.ManualKeystroke, "Manual keystroke capture")
        };

        private CancellationTokenSource? _cts;
        private Task? _loopTask;
        private GlobalKeyboardHook? _keyboardHook;
        private bool _isCapturing;
        private CaptureMode _activeCaptureMode = CaptureMode.AutoTimed;
        private IntPtr _activeHandle;
        private string _activeDataset = "";
        private int _nextImageNumber;
        private int _captureCount;

        private ComboBox CaptureModeSelector => (ComboBox)FindName("CaptureModeComboBox");
        private StackPanel IntervalSettingsPanel => (StackPanel)FindName("IntervalPanel");
        private StackPanel ManualKeySettingsPanel => (StackPanel)FindName("ManualKeyPanel");
        private TextBox ManualKeyInput => (TextBox)FindName("ManualKeyTextBox");
        private TextBlock CaptureModeStatusLabel => (TextBlock)FindName("ModeLabel");

        public MainWindow()
        {
            InitializeComponent();
            ConfigureCaptureModes();
            ApplySettings(AppCaptureSettingsStore.LoadOrDefault());
            UpdateUIState();
        }

        private void ConfigureCaptureModes()
        {
            CaptureModeSelector.ItemsSource = _captureModes;
            CaptureModeSelector.DisplayMemberPath = nameof(CaptureModeOption.DisplayName);
            CaptureModeSelector.SelectedValuePath = nameof(CaptureModeOption.Mode);
            CaptureModeSelector.SelectedValue = CaptureMode.AutoTimed;
        }

        private void ApplySettings(AppCaptureSettings settings)
        {
            var sanitized = SanitizeSettings(settings);
            DatasetNameTextBox.Text = sanitized.DatasetName;
            IntervalTextBox.Text = sanitized.IntervalSeconds.ToString(CultureInfo.InvariantCulture);
            ManualKeyInput.Text = sanitized.ManualKey;
            CaptureModeSelector.SelectedValue = sanitized.CaptureMode;
            RefreshProcessList(sanitized.SelectedProcessId);
            UpdateCaptureModeInputs();
        }

        private static AppCaptureSettings SanitizeSettings(AppCaptureSettings settings)
        {
            var sanitized = new AppCaptureSettings
            {
                SelectedProcessId = settings.SelectedProcessId,
                DatasetName = DatasetWriter.IsValidDatasetName(settings.DatasetName?.Trim() ?? "")
                    ? settings.DatasetName.Trim()
                    : "",
                CaptureMode = Enum.IsDefined(typeof(CaptureMode), settings.CaptureMode)
                    ? settings.CaptureMode
                    : CaptureMode.AutoTimed,
                IntervalSeconds = settings.IntervalSeconds > 0 ? settings.IntervalSeconds : 1,
                ManualKey = TryParseManualKey(settings.ManualKey, out var key)
                    ? NormalizeManualKeyText(key)
                    : "K"
            };

            return sanitized;
        }

        private void RefreshProcessList(int? keepProcessId)
        {
            var processes = ProcessSelector.GetAvailableProcesses();
            ProcessComboBox.ItemsSource = processes;
            var index = keepProcessId.HasValue ? processes.FindIndex(p => p.ProcessId == keepProcessId.Value) : -1;
            ProcessComboBox.SelectedIndex = index >= 0 ? index : (processes.Count > 0 ? 0 : -1);
        }

        private void ProcessComboBox_DropDownOpened(object? sender, EventArgs e)
        {
            RefreshProcessList((ProcessComboBox.SelectedItem as ProcessInfo)?.ProcessId);
            UpdateUIState();
        }

        private void CaptureModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateCaptureModeInputs();
            UpdateUIState();
        }

        private void UpdateCaptureModeInputs()
        {
            var isAutoTimed = GetSelectedCaptureMode() == CaptureMode.AutoTimed;
            IntervalSettingsPanel.Visibility = isAutoTimed ? Visibility.Visible : Visibility.Collapsed;
            ManualKeySettingsPanel.Visibility = isAutoTimed ? Visibility.Collapsed : Visibility.Visible;
        }

        private CaptureMode GetSelectedCaptureMode()
        {
            return CaptureModeSelector.SelectedValue is CaptureMode mode
                ? mode
                : CaptureMode.AutoTimed;
        }

        private string ValidateInputs(out CaptureStartOptions? options)
        {
            options = null;

            if (ProcessComboBox.SelectedItem is not ProcessInfo selected)
                return "Please select a process.";

            var datasetName = DatasetNameTextBox.Text?.Trim() ?? "";
            if (datasetName.Length == 0)
                return "Dataset/class name cannot be empty.";

            if (!DatasetWriter.IsValidDatasetName(datasetName))
                return "Dataset name contains invalid characters.";

            var mode = GetSelectedCaptureMode();
            var intervalSeconds = 1;
            var manualKey = Key.None;

            if (mode == CaptureMode.AutoTimed)
            {
                if (!int.TryParse(IntervalTextBox.Text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out intervalSeconds) || intervalSeconds <= 0)
                    return "Screenshot interval must be a positive integer.";
            }
            else if (!TryParseManualKey(ManualKeyInput.Text, out manualKey))
            {
                return "Manual capture key is invalid. Use a single key such as K, F8, Enter, or Space.";
            }

            var handle = ProcessSelector.GetWindowHandleForProcessId(selected.ProcessId);
            if (handle == IntPtr.Zero)
                handle = selected.WindowHandle;
            if (handle == IntPtr.Zero)
                return "Selected process window is no longer available.";

            options = new CaptureStartOptions(selected, datasetName, mode, intervalSeconds, manualKey, handle);
            return "";
        }

        private async void ToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isCapturing)
                await StopCaptureAsync();
            else
                StartCapture();
        }

        private void StartCapture()
        {
            ErrorMessageBlock.Text = "";

            var error = ValidateInputs(out var options);
            if (error.Length > 0 || options == null)
            {
                ErrorMessageBlock.Text = error;
                return;
            }

            int nextNumber;
            try
            {
                DatasetWriter.EnsureDatasetFolderExists(options.DatasetName);
                nextNumber = DatasetWriter.GetNextImageNumber(options.DatasetName);
            }
            catch (Exception ex)
            {
                ErrorMessageBlock.Text = $"Failed to prepare dataset folder: {ex.Message}";
                return;
            }

            var cts = new CancellationTokenSource();
            GlobalKeyboardHook? keyboardHook = null;

            try
            {
                if (options.Mode == CaptureMode.ManualKeystroke)
                {
                    keyboardHook = new GlobalKeyboardHook(options.ManualKey, () => _ = CaptureSingleFrameAsync(cts));
                    keyboardHook.Start();
                }
            }
            catch (Exception ex)
            {
                keyboardHook?.Dispose();
                ErrorMessageBlock.Text = $"Failed to start manual key listener: {ex.Message}";
                return;
            }

            _cts = cts;
            _keyboardHook = keyboardHook;
            _activeCaptureMode = options.Mode;
            _activeHandle = options.Handle;
            _activeDataset = options.DatasetName;
            _nextImageNumber = nextNumber;
            _captureCount = 0;
            _isCapturing = true;

            ProcessLabel.Text = options.SelectedProcess.DisplayName;
            DatasetLabel.Text = options.DatasetName;
            CaptureModeStatusLabel.Text = GetCaptureModeDisplayName(options.Mode);
            CapturedLabel.Text = "0";
            LastFileLabel.Text = "-";
            UpdateUIState();
            TrySaveCurrentSettings();

            _loopTask = options.Mode == CaptureMode.AutoTimed
                ? Task.Run(() => CaptureLoopAsync(options.IntervalSeconds, cts))
                : Task.Run(() => WaitForCancellationAsync(cts.Token));
        }

        private async Task StopCaptureAsync()
        {
            _isCapturing = false;
            var cts = _cts;
            _cts = null;
            DisposeKeyboardHook();
            cts?.Cancel();

            ToggleButton.IsEnabled = false;
            var loop = _loopTask;
            if (loop != null)
                await loop;
            ToggleButton.IsEnabled = true;
            UpdateUIState();
        }

        private async Task CaptureLoopAsync(int intervalSeconds, CancellationTokenSource cts)
        {
            var token = cts.Token;

            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));
                do
                {
                    if (!await CaptureFrameAsync(cts, skipIfBusy: false))
                        break;
                }
                while (await timer.WaitForNextTickAsync(token));
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task CaptureSingleFrameAsync(CancellationTokenSource cts)
        {
            try
            {
                await CaptureFrameAsync(cts, skipIfBusy: true);
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task<bool> CaptureFrameAsync(CancellationTokenSource cts, bool skipIfBusy)
        {
            var token = cts.Token;
            var lockTaken = false;

            try
            {
                if (skipIfBusy)
                {
                    lockTaken = await _captureLock.WaitAsync(0, token);
                    if (!lockTaken)
                        return true;
                }
                else
                {
                    await _captureLock.WaitAsync(token);
                    lockTaken = true;
                }

                using var bitmap = ScreenshotCapture.CaptureWindow(_activeHandle);
                if (bitmap == null)
                {
                    await HandleCaptureFailureAsync(cts, "Failed to capture screenshot (window closed, minimized or invalid).");
                    return false;
                }

                token.ThrowIfCancellationRequested();
                var filename = DatasetWriter.SaveScreenshot(_activeDataset, _nextImageNumber++, bitmap);
                _captureCount++;
                var saved = _captureCount;

                _ = Dispatcher.BeginInvoke(() =>
                {
                    if (ReferenceEquals(_cts, cts))
                    {
                        CapturedLabel.Text = saved.ToString(CultureInfo.InvariantCulture);
                        LastFileLabel.Text = filename;
                    }
                });

                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                await HandleCaptureFailureAsync(cts, $"Capture error: {ex.Message}");
                return false;
            }
            finally
            {
                if (lockTaken)
                    _captureLock.Release();
            }
        }

        private async Task HandleCaptureFailureAsync(CancellationTokenSource cts, string message)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(_cts, cts))
                {
                    ErrorMessageBlock.Text = message;
                    _ = StopCaptureAsync();
                }
            });
        }

        private static async Task WaitForCancellationAsync(CancellationToken token)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
            }
        }

        private void DisposeKeyboardHook()
        {
            _keyboardHook?.Dispose();
            _keyboardHook = null;
        }

        private void TrySaveCurrentSettings()
        {
            try
            {
                AppCaptureSettingsStore.Save(BuildCurrentSettings());
            }
            catch
            {
            }
        }

        private AppCaptureSettings BuildCurrentSettings()
        {
            var settings = new AppCaptureSettings
            {
                SelectedProcessId = (ProcessComboBox.SelectedItem as ProcessInfo)?.ProcessId,
                DatasetName = DatasetWriter.IsValidDatasetName(DatasetNameTextBox.Text?.Trim() ?? "")
                    ? DatasetNameTextBox.Text.Trim()
                    : "",
                CaptureMode = GetSelectedCaptureMode(),
                IntervalSeconds = int.TryParse(IntervalTextBox.Text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var interval) && interval > 0
                    ? interval
                    : 1,
                ManualKey = TryParseManualKey(ManualKeyInput.Text, out var key)
                    ? NormalizeManualKeyText(key)
                    : "K"
            };

            return settings;
        }

        private static bool TryParseManualKey(string? text, out Key key)
        {
            key = Key.None;
            var value = text?.Trim();
            if (string.IsNullOrWhiteSpace(value))
                return false;

            if (value.Length == 1)
            {
                var singleCharacter = char.ToUpperInvariant(value[0]).ToString();
                if (Enum.TryParse(singleCharacter, true, out key) && IsSupportedManualKey(key))
                    return true;
            }

            var converter = new KeyConverter();
            if (converter.ConvertFromInvariantString(value) is Key convertedKey && IsSupportedManualKey(convertedKey))
            {
                key = convertedKey;
                return true;
            }

            return false;
        }

        private static bool IsSupportedManualKey(Key key)
        {
            return key is not Key.None
                and not Key.LeftCtrl
                and not Key.RightCtrl
                and not Key.LeftAlt
                and not Key.RightAlt
                and not Key.LeftShift
                and not Key.RightShift
                and not Key.LWin
                and not Key.RWin;
        }

        private static string NormalizeManualKeyText(Key key)
        {
            return key.ToString();
        }

        private void UpdateUIState()
        {
            var selectedMode = _isCapturing ? _activeCaptureMode : GetSelectedCaptureMode();

            ToggleButton.Content = _isCapturing ? "Stop" : "Start";
            ToggleButton.Background = _isCapturing ? Brushes.OrangeRed : Brushes.CornflowerBlue;
            ProcessComboBox.IsEnabled = !_isCapturing;
            DatasetNameTextBox.IsEnabled = !_isCapturing;
            CaptureModeSelector.IsEnabled = !_isCapturing;
            IntervalTextBox.IsEnabled = !_isCapturing && selectedMode == CaptureMode.AutoTimed;
            ManualKeyInput.IsEnabled = !_isCapturing && selectedMode == CaptureMode.ManualKeystroke;
            StateLabel.Text = _isCapturing
                ? selectedMode == CaptureMode.AutoTimed ? "Capturing" : "Listening"
                : "Stopped";
            CaptureModeStatusLabel.Text = GetCaptureModeDisplayName(selectedMode);

            if (!_isCapturing)
            {
                ProcessLabel.Text = (ProcessComboBox.SelectedItem as ProcessInfo)?.DisplayName ?? "-";
                DatasetLabel.Text = string.IsNullOrWhiteSpace(DatasetNameTextBox.Text) ? "-" : DatasetNameTextBox.Text.Trim();
            }
        }

        private static string GetCaptureModeDisplayName(CaptureMode mode)
        {
            return mode == CaptureMode.AutoTimed
                ? "Auto timed screenshot"
                : "Manual keystroke capture";
        }

        protected override void OnClosed(EventArgs e)
        {
            TrySaveCurrentSettings();
            DisposeKeyboardHook();
            _cts?.Cancel();
            base.OnClosed(e);
        }

        private sealed record CaptureModeOption(CaptureMode Mode, string DisplayName);

        private sealed record CaptureStartOptions(
            ProcessInfo SelectedProcess,
            string DatasetName,
            CaptureMode Mode,
            int IntervalSeconds,
            Key ManualKey,
            IntPtr Handle);
    }
}
