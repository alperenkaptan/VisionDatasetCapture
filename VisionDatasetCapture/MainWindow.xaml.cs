using System;
using System.Globalization;
using System.IO;
using System.Drawing;
using System.Drawing.Imaging;
using DrawingBitmap = System.Drawing.Bitmap;
using System.Windows;
using System.Windows.Controls;
using WPFImage = System.Windows.Controls.Image;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WPFBrushes = System.Windows.Media.Brushes;
using Microsoft.Win32;

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

        private CancellationTokenSource? _previewCts;  // Controls preview streaming
        private CancellationTokenSource? _captureCts;  // Controls dataset screenshot capture
        private Task? _loopTask;
        private Task? _previewLoopTask;
        private GlobalKeyboardHook? _keyboardHook;
        private bool _isCapturing; // Preview streaming is active
        private bool _isCapturingDataset; // Actually taking screenshots
        private CaptureMode _activeCaptureMode = CaptureMode.AutoTimed;
        private IntPtr _activeHandle;
        private string _activeDataset = "";
        private int _nextImageNumber;
        private int _captureCount;

        private Bitmap? _latestProcessedFrame;
        private Bitmap? _latestRawCapturedFrame;
        private PostProcessingSettings _currentPostProcessingSettings = new();
        private ColorEffectsSettings _currentColorEffectsSettings = new();
        private bool _isFreezeFrameActive;
        private bool _isUpdatingColorInputs;
        private bool _isUpdatingColorEffectsNumericInputs;
        private readonly DispatcherTimer _colorEffectsUpdateDebounceTimer = new()
        {
            Interval = TimeSpan.FromMilliseconds(75)
        };

        // Zoom state
        private double _zoomLevel = 1.0;
        private const double ZoomIncrement = 0.1;
        private const double MinZoom = 0.1;
        private const double MaxZoom = 5.0;
        private bool _isPanning;
        private System.Windows.Point _lastPanPoint;
        private double _panX;
        private double _panY;

        // Process info
        private ProcessWindowInfo? _currentProcessInfo;

        // Eyedropper state
        private bool _isEyedropperActive;
        private bool _wasLeftMouseDown;
        private CancellationTokenSource? _eyedropperCts;
        private Task? _eyedropperLoopTask;
        private Window? _eyedropperOverlay;
        private System.Drawing.Color? _selectedColor;
        private System.Windows.Point? _lastSamplePoint;
        private const int MagnifierSampleRadius = 5;
        private const int MagnifierScale = 10;

        private ComboBox CaptureModeSelector => (ComboBox)FindName("CaptureModeComboBox");
        private StackPanel IntervalSettingsPanel => (StackPanel)FindName("IntervalPanel");
        private StackPanel ManualKeySettingsPanel => (StackPanel)FindName("ManualKeyPanel");
        private TextBox ManualKeyInput => (TextBox)FindName("ManualKeyTextBox");
        private TextBlock CaptureModeStatusLabel => (TextBlock)FindName("ModeLabel");

        public MainWindow()
        {
            _colorEffectsUpdateDebounceTimer.Tick += ColorEffectsUpdateDebounceTimer_Tick;

            InitializeComponent();

            ConfigureCaptureModes();
            ConfigurePostProcessingUI();
            ApplySettings(AppCaptureSettingsStore.LoadOrDefault());
            UpdateUIState();
            PreviewKeyDown += MainWindow_PreviewKeyDown;
        }

        private async void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // Gracefully shutdown any active capture/preview
            if (_isCapturingDataset || _isCapturing)
            {
                e.Cancel = true;

                try
                {
                    if (_isCapturingDataset)
                        await StopCaptureAsync();

                    if (_isCapturing)
                        await StopPreviewAsync();
                }
                finally
                {
                    // Save settings before closing
                    TrySaveCurrentSettings();

                    // Cleanup resources
                    _latestProcessedFrame?.Dispose();
                    _latestRawCapturedFrame?.Dispose();
                    _keyboardHook?.Dispose();
                    _previewCts?.Dispose();
                    _captureCts?.Dispose();
                    _colorEffectsUpdateDebounceTimer.Stop();

                    Close();
                }
            }
            else
            {
                // Save settings even if no active capture/preview
                TrySaveCurrentSettings();
            }
        }

        private void ConfigureCaptureModes()
        {
            CaptureModeSelector.ItemsSource = _captureModes;
            CaptureModeSelector.DisplayMemberPath = nameof(CaptureModeOption.DisplayName);
            CaptureModeSelector.SelectedValuePath = nameof(CaptureModeOption.Mode);
            CaptureModeSelector.SelectedValue = CaptureMode.AutoTimed;
        }

        private void ConfigurePostProcessingUI()
        {
            // Set default values
            var postProc = _currentPostProcessingSettings;
            ((CheckBox)FindName("PostProcessingEnabledCheckBox")).IsChecked = postProc.Enabled;
            ((CheckBox)FindName("GrayscaleCheckBox")).IsChecked = postProc.Grayscale;
            ((Slider)FindName("BrightnessSlider")).Value = postProc.Brightness;
            ((Slider)FindName("ContrastSlider")).Value = postProc.Contrast;
            ((Slider)FindName("SaturationSlider")).Value = postProc.Saturation;
            ((Slider)FindName("GammaSlider")).Value = postProc.Gamma;
            ((CheckBox)FindName("CropEnabledCheckBox")).IsChecked = postProc.Crop.Enabled;
            ((TextBox)FindName("CropXTextBox")).Text = postProc.Crop.X.ToString();
            ((TextBox)FindName("CropYTextBox")).Text = postProc.Crop.Y.ToString();
            ((TextBox)FindName("CropWidthTextBox")).Text = postProc.Crop.Width.ToString();
            ((TextBox)FindName("CropHeightTextBox")).Text = postProc.Crop.Height.ToString();
            ((CheckBox)FindName("ResizeEnabledCheckBox")).IsChecked = postProc.Resize.Enabled;
            ((TextBox)FindName("ResizeWidthTextBox")).Text = postProc.Resize.Width.ToString();
            ((TextBox)FindName("ResizeHeightTextBox")).Text = postProc.Resize.Height.ToString();
        }

        private void ConfigureColorEffectsUI()
        {
            var colorFx = _currentColorEffectsSettings;

            ((CheckBox)FindName("ColorEffectsEnabledCheckBox")).IsChecked = colorFx.Enabled;
            ((CheckBox)FindName("ColorEffectsMaskOverlayCheckBox")).IsChecked = colorFx.ShowMaskOverlay;
            ((CheckBox)FindName("ColorEffectsFreezeFrameCheckBox")).IsChecked = colorFx.FreezeFrame;
            ((Slider)FindName("ColorEffectsHueToleranceSlider")).Value = colorFx.HueTolerance;
            ((Slider)FindName("ColorEffectsSaturationToleranceSlider")).Value = colorFx.SaturationTolerance;
            ((Slider)FindName("ColorEffectsValueToleranceSlider")).Value = colorFx.ValueTolerance;
            ((Slider)FindName("ColorEffectsStrengthSlider")).Value = colorFx.EffectStrength;
            ((TextBox)FindName("ColorEffectsTargetHexTextBox")).Text = $"#{colorFx.TargetR:X2}{colorFx.TargetG:X2}{colorFx.TargetB:X2}";

            var modeCombo = (ComboBox)FindName("ColorEffectsModeComboBox");
            modeCombo.ItemsSource = Enum.GetValues(typeof(ColorEffectMode));
            modeCombo.SelectedItem = colorFx.EffectMode;

            var hueInput = (TextBox)FindName("ColorEffectsHueToleranceInput");
            var satInput = (TextBox)FindName("ColorEffectsSaturationToleranceInput");
            var valInput = (TextBox)FindName("ColorEffectsValueToleranceInput");
            var strengthInput = (TextBox)FindName("ColorEffectsStrengthInput");
            hueInput.Text = colorFx.HueTolerance.ToString("F0", CultureInfo.InvariantCulture);
            satInput.Text = colorFx.SaturationTolerance.ToString("F2", CultureInfo.InvariantCulture);
            valInput.Text = colorFx.ValueTolerance.ToString("F2", CultureInfo.InvariantCulture);
            strengthInput.Text = colorFx.EffectStrength.ToString("F2", CultureInfo.InvariantCulture);
        }

        private void SyncSelectedColorFromColorEffectsSettings()
        {
            var selected = System.Drawing.Color.FromArgb(255,
                _currentColorEffectsSettings.BaseR,
                _currentColorEffectsSettings.BaseG,
                _currentColorEffectsSettings.BaseB);

            _selectedColor = selected;
            UpdateSelectedColorSwatch(selected);
            UpdateColorReadout(selected, _lastSamplePoint.HasValue ? (int)_lastSamplePoint.Value.X : 0, _lastSamplePoint.HasValue ? (int)_lastSamplePoint.Value.Y : 0);
            UpdateColorInputsFromSelectedColor(selected);
            UpdateMagnifierToSolidColor(selected);
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

            // Load post-processing settings
            _currentPostProcessingSettings = sanitized.PostProcessing.Clone();
            ConfigurePostProcessingUI();

            // Load color effects settings (separate stage)
            _currentColorEffectsSettings = (sanitized.ColorEffects ?? new ColorEffectsSettings()).Clone();
            ConfigureColorEffectsUI();
            SyncSelectedColorFromColorEffectsSettings();

            // Load zoom level
            _zoomLevel = sanitized.ZoomLevel;
            UpdateZoomLevel();
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
                    : "K",
                PostProcessing = settings.PostProcessing ?? new PostProcessingSettings(),
                ColorEffects = settings.ColorEffects ?? new ColorEffectsSettings(),
                ZoomLevel = settings.ZoomLevel >= 0.1 && settings.ZoomLevel <= 5.0 ? settings.ZoomLevel : 1.0
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

        private async void ProcessComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            UpdateUIState();
        }

        private void ProcessComboBox_DropDownOpened(object? sender, EventArgs e)
        {
            RefreshProcessList((ProcessComboBox.SelectedItem as ProcessInfo)?.ProcessId);
        }

        private async void StartPreviewButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isCapturing)
                await StopPreviewAsync();
            else
                StartPreview();
        }

        private void StartPreview()
        {
            // Stop any existing preview
            if (ProcessComboBox.SelectedItem is not ProcessInfo selected)
                return;

            var handle = ProcessSelector.GetWindowHandleForProcessId(selected.ProcessId);
            if (handle == IntPtr.Zero)
                handle = selected.WindowHandle;

            if (handle == IntPtr.Zero)
                return;

            _isCapturing = true;
            _activeHandle = handle;

            // Create or reuse preview CTS
            if (_previewCts == null)
            {
                _previewCts = new CancellationTokenSource();
            }

            _previewLoopTask = Task.Run(() => PreviewLoopAsync(_previewCts));
            UpdateUIState();
        }

        private void CaptureModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateCaptureModeInputs();
            UpdateUIState();
            TrySaveCurrentSettings();
        }

        private void PreviewImage_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            e.Handled = true;

            // Zoom based on scroll direction
            double zoomDelta = e.Delta > 0 ? ZoomIncrement : -ZoomIncrement;
            double newZoom = _zoomLevel + zoomDelta;

            // Clamp zoom to valid range
            newZoom = Math.Max(MinZoom, Math.Min(MaxZoom, newZoom));

            _zoomLevel = newZoom;
            if (_zoomLevel <= 1.0)
            {
                _panX = 0;
                _panY = 0;
            }

            UpdateZoomLevel();
        }

        private void ZoomResetButton_Click(object sender, RoutedEventArgs e)
        {
            _zoomLevel = 1.0;
            _panX = 0;
            _panY = 0;
            UpdateZoomLevel();
        }

        private void EyedropperButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_isCapturing)
            {
                SetEyedropperUiState("Start capture first", WPFBrushes.Gray);
                return;
            }

            if (_isEyedropperActive)
            {
                CancelEyedropperMode();
                return;
            }

            StartExternalEyedropperMode();
        }

        private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && _isEyedropperActive)
            {
                CancelEyedropperMode();
                e.Handled = true;
            }
        }

        private void PreviewBorder_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Middle && _isCapturing && _zoomLevel > 1.0)
            {
                _isPanning = true;
                _lastPanPoint = e.GetPosition(PreviewBorder);
                PreviewBorder.CaptureMouse();
                e.Handled = true;
            }
        }

        private void PreviewBorder_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Middle && _isPanning)
            {
                _isPanning = false;
                PreviewBorder.ReleaseMouseCapture();
                e.Handled = true;
            }
        }

        private void StartExternalEyedropperMode()
        {
            _isEyedropperActive = true;
            _wasLeftMouseDown = false;

            if (Win32Interop.GetWindowRect(_activeHandle, out var rect))
            {
                var centerX = (rect.Left + rect.Right) / 2;
                var centerY = (rect.Top + rect.Bottom) / 2;
                Win32Interop.SetCursorPos(centerX, centerY);
            }

            // Keep app behind target process while sampling from target surface.
            Topmost = false;

            Focus();
            SetEyedropperUiState("Sampling target process... Left click to select, Esc to cancel", WPFBrushes.DodgerBlue);
            OpenEyedropperOverlay();
        }

        private void CancelEyedropperMode()
        {
            _isEyedropperActive = false;

            var cts = _eyedropperCts;
            _eyedropperCts = null;
            cts?.Cancel();
            cts?.Dispose();

            _eyedropperLoopTask = null;

            if (_eyedropperOverlay != null)
            {
                try
                {
                    _eyedropperOverlay.Close();
                }
                catch
                {
                }
                _eyedropperOverlay = null;
            }

            var popup = FindName("FloatingMagnifierPopup") as System.Windows.Controls.Primitives.Popup;
            if (popup != null)
                popup.IsOpen = false;

            // Restore app topmost behavior after eyedropper completes/cancels.
            Topmost = true;

            SetEyedropperUiState("Cancelled", WPFBrushes.Gray);
        }

        private void UpdateZoomLevel()
        {
            var zoomTransform = FindName("ZoomTransform") as System.Windows.Media.ScaleTransform;
            if (zoomTransform != null)
            {
                zoomTransform.ScaleX = _zoomLevel;
                zoomTransform.ScaleY = _zoomLevel;
            }

            var panTransform = FindName("PanTransform") as System.Windows.Media.TranslateTransform;
            if (panTransform != null)
            {
                panTransform.X = _zoomLevel > 1.0 ? _panX : 0;
                panTransform.Y = _zoomLevel > 1.0 ? _panY : 0;
            }

            var zoomLabel = FindName("ZoomLabel") as TextBlock;
            if (zoomLabel != null)
            {
                zoomLabel.Text = $"{(int)(_zoomLevel * 100)}%";
            }

            // Auto-save zoom level
            TrySaveCurrentSettings();
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
            if (_isCapturingDataset)
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

            var captureCts = new CancellationTokenSource();
            GlobalKeyboardHook? keyboardHook = null;

            try
            {
                if (options.Mode == CaptureMode.ManualKeystroke)
                {
                    keyboardHook = new GlobalKeyboardHook(options.ManualKey, () => _ = CaptureSingleFrameAsync(captureCts));
                    keyboardHook.Start();
                }
            }
            catch (Exception ex)
            {
                keyboardHook?.Dispose();
                ErrorMessageBlock.Text = $"Failed to start manual key listener: {ex.Message}";
                return;
            }

            _captureCts = captureCts;
            _keyboardHook = keyboardHook;
            _activeCaptureMode = options.Mode;
            _activeHandle = options.Handle;
            _activeDataset = options.DatasetName;
            _nextImageNumber = nextNumber;
            _captureCount = 0;
            _isCapturingDataset = true;

            ProcessLabel.Text = options.SelectedProcess.DisplayName;
            DatasetLabel.Text = options.DatasetName;
            CaptureModeStatusLabel.Text = GetCaptureModeDisplayName(options.Mode);
            CapturedLabel.Text = "0";
            LastFileLabel.Text = "-";
            UpdateUIState();
            TrySaveCurrentSettings();

            _loopTask = options.Mode == CaptureMode.AutoTimed
                ? Task.Run(() => CaptureLoopAsync(options.IntervalSeconds, captureCts))
                : Task.Run(() => WaitForCancellationAsync(captureCts.Token));
        }

        private async Task StopCaptureAsync()
        {
            _isCapturingDataset = false;
            var captureCts = _captureCts;
            _captureCts = null;
            DisposeKeyboardHook();
            captureCts?.Cancel();

            ToggleButton.IsEnabled = false;
            var loop = _loopTask;
            if (loop != null)
                await loop;
            ToggleButton.IsEnabled = true;
            UpdateUIState();
        }

        private async Task StopPreviewAsync()
        {
            _isCapturing = false;
            _isFreezeFrameActive = false;
            var previewCts = _previewCts;
            _previewCts = null;
            previewCts?.Cancel();

            var previewLoop = _previewLoopTask;
            if (previewLoop != null)
                await previewLoop;

            CancelEyedropperMode();

            // Reset zoom when stopping capture
            _zoomLevel = 1.0;
            _panX = 0;
            _panY = 0;
            _isPanning = false;
            UpdateZoomLevel();

            UpdateUIState();
        }

        private async Task CaptureLoopAsync(int intervalSeconds, CancellationTokenSource captureCts)
        {
            var token = captureCts.Token;

            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));
                do
                {
                    if (!await CaptureFrameAsync(captureCts, skipIfBusy: false))
                        break;
                }
                while (await timer.WaitForNextTickAsync(token));
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task CaptureSingleFrameAsync(CancellationTokenSource captureCts)
        {
            try
            {
                await CaptureFrameAsync(captureCts, skipIfBusy: true);
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task PreviewLoopAsync(CancellationTokenSource previewCts)
        {
            var token = previewCts.Token;

            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(33)); // ~30fps
                do
                {
                    try
                    {
                        if (_isFreezeFrameActive)
                        {
                            await Task.Delay(33, token);
                            continue;
                        }

                        // Non-blocking capture - just get the latest frame without waiting for lock
                        using var bitmap = ScreenshotCapture.CaptureWindow(_activeHandle);
                        if (bitmap != null)
                        {
                            try
                            {
                                // Try to acquire lock with timeout to avoid blocking the preview loop
                                bool lockAcquired = await _captureLock.WaitAsync(100, token);
                                if (lockAcquired)
                                {
                                    try
                                    {
                                        _latestRawCapturedFrame?.Dispose();
                                        _latestRawCapturedFrame = (Bitmap)bitmap.Clone();

                                        var settingsSnapshot = _currentPostProcessingSettings.Clone();
                                        var colorFxSnapshot = _currentColorEffectsSettings.Clone();
                                        using var colorFxFirst = ColorEffectsProcessor.Apply(bitmap, colorFxSnapshot);
                                        using var processed = ImageProcessor.Process(colorFxFirst, settingsSnapshot);

                                        _latestProcessedFrame?.Dispose();
                                        _latestProcessedFrame = (DrawingBitmap)processed.Clone();
                                    }
                                    finally
                                    {
                                        _captureLock.Release();
                                    }
                                }
                                // else: lock is busy, skip this frame update but continue

                                // Dispatch to UI with owned copies (don't hold lock during dispatch)
                                if (_latestProcessedFrame != null)
                                {
                                    var processedCopy = (Bitmap)_latestProcessedFrame.Clone();

                                    _ = Dispatcher.BeginInvoke(() =>
                                    {
                                        try
                                        {
                                            if (ReferenceEquals(_previewCts, previewCts))
                                            {
                                                UpdatePreview(processedCopy);
                                            }
                                            else
                                            {
                                                processedCopy?.Dispose();
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            System.Diagnostics.Debug.WriteLine($"Error updating preview: {ex}");
                                            processedCopy?.Dispose();
                                        }
                                    });
                                }
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"Error processing frame: {ex}");
                            }
                        }
                        else
                        {
                            // Window closed or became invalid
                            if (_isCapturingDataset || _isCapturing)
                            {
                                _ = Dispatcher.BeginInvoke(async () =>
                                {
                                    try
                                    {
                                        ErrorMessageBlock.Text = "Target window closed or is no longer accessible.";
                                        if (_isCapturingDataset)
                                            await StopCaptureAsync();
                                        if (_isCapturing)
                                            await StopPreviewAsync();
                                    }
                                    catch { }
                                });
                            }
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error in preview loop iteration: {ex}");
                    }
                }
                while (await timer.WaitForNextTickAsync(token));
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Fatal error in PreviewLoopAsync: {ex}");
            }
        }

        private async Task<bool> CaptureFrameAsync(CancellationTokenSource captureCts, bool skipIfBusy)
        {
            var token = captureCts.Token;
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

                using var rawBitmap = ScreenshotCapture.CaptureWindow(_activeHandle);
                if (rawBitmap == null)
                {
                    await HandleCaptureFailureAsync(captureCts, "Failed to capture screenshot (window closed, minimized or invalid).");
                    return false;
                }

                token.ThrowIfCancellationRequested();

                _latestRawCapturedFrame?.Dispose();
                _latestRawCapturedFrame = (Bitmap)rawBitmap.Clone();

                // Process the frame using specific-to-general order:
                // 1) color effects on raw image, 2) global post-processing on the result
                var settingsSnapshot = _currentPostProcessingSettings.Clone();
                var colorFxSnapshot = _currentColorEffectsSettings.Clone();
                using var colorEffectsFirstBitmap = ColorEffectsProcessor.Apply(rawBitmap, colorFxSnapshot);
                using var processedBitmap = ImageProcessor.Process(colorEffectsFirstBitmap, settingsSnapshot);

                token.ThrowIfCancellationRequested();

                // Update processed frame for preview
                _latestProcessedFrame?.Dispose();
                _latestProcessedFrame = (Bitmap)processedBitmap.Clone();

                // Save the processed bitmap to disk
                var filename = DatasetWriter.SaveScreenshot(_activeDataset, _nextImageNumber++, processedBitmap);
                _captureCount++;
                var saved = _captureCount;

                // Make safe copy for preview (while holding lock)
                var processedCopy = (Bitmap)processedBitmap.Clone();

                // Update UI
                _ = Dispatcher.BeginInvoke(() =>
                {
                    if (ReferenceEquals(_captureCts, captureCts))
                    {
                        CapturedLabel.Text = saved.ToString(CultureInfo.InvariantCulture);
                        LastFileLabel.Text = filename;
                        UpdatePreview(processedCopy); // Update preview with new frame
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
                await HandleCaptureFailureAsync(captureCts, $"Capture error: {ex.Message}");
                return false;
            }
            finally
            {
                if (lockTaken)
                    _captureLock.Release();
            }
        }

        private async Task HandleCaptureFailureAsync(CancellationTokenSource captureCts, string message)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(_captureCts, captureCts))
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
                    : "K",
                PostProcessing = _currentPostProcessingSettings.Clone(),
                ColorEffects = _currentColorEffectsSettings.Clone(),
                ZoomLevel = _zoomLevel
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

        private void PostProcessingSettings_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement) return;
            UpdatePostProcessingSettings();
        }

        private void PostProcessingSettings_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            UpdatePostProcessingSettings();
        }

        private void CropSettings_Changed(object sender, TextChangedEventArgs e)
        {
            UpdatePostProcessingSettings();
        }

        private void ResizeSettings_Changed(object sender, TextChangedEventArgs e)
        {
            UpdatePostProcessingSettings();
        }

        private void UpdatePostProcessingSettings()
        {
            // Skip if controls are not yet loaded
            var enabledCheckBox = FindName("PostProcessingEnabledCheckBox") as CheckBox;
            var grayscaleCheckBox = FindName("GrayscaleCheckBox") as CheckBox;
            var brightnessSlider = FindName("BrightnessSlider") as Slider;
            var contrastSlider = FindName("ContrastSlider") as Slider;
            var saturationSlider = FindName("SaturationSlider") as Slider;
            var gammaSlider = FindName("GammaSlider") as Slider;
            var cropEnabledCheckBox = FindName("CropEnabledCheckBox") as CheckBox;
            var cropXTextBox = FindName("CropXTextBox") as TextBox;
            var cropYTextBox = FindName("CropYTextBox") as TextBox;
            var cropWidthTextBox = FindName("CropWidthTextBox") as TextBox;
            var cropHeightTextBox = FindName("CropHeightTextBox") as TextBox;
            var resizeEnabledCheckBox = FindName("ResizeEnabledCheckBox") as CheckBox;
            var resizeWidthTextBox = FindName("ResizeWidthTextBox") as TextBox;
            var resizeHeightTextBox = FindName("ResizeHeightTextBox") as TextBox;
            var brightnessLabel = FindName("BrightnessLabel") as TextBlock;
            var contrastLabel = FindName("ContrastLabel") as TextBlock;
            var saturationLabel = FindName("SaturationLabel") as TextBlock;
            var gammaLabel = FindName("GammaLabel") as TextBlock;

            // If any critical control is not found, skip update (still loading)
            if (enabledCheckBox == null || grayscaleCheckBox == null || brightnessSlider == null ||
                contrastSlider == null || saturationSlider == null || gammaSlider == null ||
                cropEnabledCheckBox == null || resizeEnabledCheckBox == null)
                return;

            _currentPostProcessingSettings = new PostProcessingSettings
            {
                Enabled = enabledCheckBox.IsChecked ?? true,
                Grayscale = grayscaleCheckBox.IsChecked ?? false,
                Brightness = (float)brightnessSlider.Value,
                Contrast = (float)contrastSlider.Value,
                Saturation = (float)saturationSlider.Value,
                Gamma = (float)gammaSlider.Value,
                Crop = new PostProcessingCropSettings
                {
                    Enabled = cropEnabledCheckBox.IsChecked ?? false,
                    X = int.TryParse(cropXTextBox?.Text ?? "", out var x) ? x : 0,
                    Y = int.TryParse(cropYTextBox?.Text ?? "", out var y) ? y : 0,
                    Width = int.TryParse(cropWidthTextBox?.Text ?? "", out var w) ? w : 0,
                    Height = int.TryParse(cropHeightTextBox?.Text ?? "", out var h) ? h : 0
                },
                Resize = new PostProcessingResizeSettings
                {
                    Enabled = resizeEnabledCheckBox.IsChecked ?? false,
                    Width = int.TryParse(resizeWidthTextBox?.Text ?? "", out var rw) ? rw : 800,
                    Height = int.TryParse(resizeHeightTextBox?.Text ?? "", out var rh) ? rh : 600
                }
            };

            // Update labels safely
            if (brightnessLabel != null)
                brightnessLabel.Text = _currentPostProcessingSettings.Brightness.ToString("F1");
            if (contrastLabel != null)
                contrastLabel.Text = _currentPostProcessingSettings.Contrast.ToString("F1");
            if (saturationLabel != null)
                saturationLabel.Text = _currentPostProcessingSettings.Saturation.ToString("F1");
            if (gammaLabel != null)
                gammaLabel.Text = _currentPostProcessingSettings.Gamma.ToString("F1");

            if (_isFreezeFrameActive)
            {
                ReprocessAndRefreshFrozenFrame();
            }
            else
            {
                UpdatePreviewIfAvailable();
            }

            TrySaveCurrentSettings();
        }

        private void ColorEffectsSettings_Changed(object sender, RoutedEventArgs e)
        {
            UpdateColorEffectsSettingsFromUI();
        }

        private void ColorEffectsSettings_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            _colorEffectsUpdateDebounceTimer.Stop();
            _colorEffectsUpdateDebounceTimer.Start();
        }

        private void ColorEffectsTargetHexTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateColorEffectsSettingsFromUI();
        }

        private void ColorEffectsUpdateDebounceTimer_Tick(object? sender, EventArgs e)
        {
            _colorEffectsUpdateDebounceTimer.Stop();
            UpdateColorEffectsSettingsFromUI();
        }

        private void UpdateColorEffectsSettingsFromUI()
        {
            _colorEffectsUpdateDebounceTimer.Stop();

            var enabledCheck = FindName("ColorEffectsEnabledCheckBox") as CheckBox;
            var maskCheck = FindName("ColorEffectsMaskOverlayCheckBox") as CheckBox;
            var freezeCheck = FindName("ColorEffectsFreezeFrameCheckBox") as CheckBox;
            var hueSlider = FindName("ColorEffectsHueToleranceSlider") as Slider;
            var satSlider = FindName("ColorEffectsSaturationToleranceSlider") as Slider;
            var valSlider = FindName("ColorEffectsValueToleranceSlider") as Slider;
            var modeCombo = FindName("ColorEffectsModeComboBox") as ComboBox;
            var strengthSlider = FindName("ColorEffectsStrengthSlider") as Slider;
            var targetHexText = FindName("ColorEffectsTargetHexTextBox") as TextBox;
            var hueInput = FindName("ColorEffectsHueToleranceInput") as TextBox;
            var satInput = FindName("ColorEffectsSaturationToleranceInput") as TextBox;
            var valInput = FindName("ColorEffectsValueToleranceInput") as TextBox;
            var strengthInput = FindName("ColorEffectsStrengthInput") as TextBox;

            if (enabledCheck == null || maskCheck == null || freezeCheck == null || hueSlider == null || satSlider == null ||
                valSlider == null || modeCombo == null || strengthSlider == null || targetHexText == null)
                return;

            var targetColor = TryParseHexColor(targetHexText.Text, out var tr, out var tg, out var tb)
                ? (TargetR: tr, TargetG: tg, TargetB: tb)
                : (TargetR: _currentColorEffectsSettings.TargetR, TargetG: _currentColorEffectsSettings.TargetG, TargetB: _currentColorEffectsSettings.TargetB);

            _currentColorEffectsSettings = new ColorEffectsSettings
            {
                Enabled = enabledCheck.IsChecked ?? false,
                ShowMaskOverlay = maskCheck.IsChecked ?? false,
                FreezeFrame = freezeCheck.IsChecked ?? false,
                BaseR = _currentColorEffectsSettings.BaseR,
                BaseG = _currentColorEffectsSettings.BaseG,
                BaseB = _currentColorEffectsSettings.BaseB,
                HueTolerance = hueSlider.Value,
                SaturationTolerance = satSlider.Value,
                ValueTolerance = valSlider.Value,
                EffectMode = modeCombo.SelectedItem is ColorEffectMode mode ? mode : ColorEffectMode.Highlight,
                EffectStrength = strengthSlider.Value,
                TargetR = targetColor.TargetR,
                TargetG = targetColor.TargetG,
                TargetB = targetColor.TargetB
            };

            _isUpdatingColorEffectsNumericInputs = true;
            try
            {
                if (hueInput != null)
                    hueInput.Text = _currentColorEffectsSettings.HueTolerance.ToString("F0", CultureInfo.InvariantCulture);
                if (satInput != null)
                    satInput.Text = _currentColorEffectsSettings.SaturationTolerance.ToString("F2", CultureInfo.InvariantCulture);
                if (valInput != null)
                    valInput.Text = _currentColorEffectsSettings.ValueTolerance.ToString("F2", CultureInfo.InvariantCulture);
                if (strengthInput != null)
                    strengthInput.Text = _currentColorEffectsSettings.EffectStrength.ToString("F2", CultureInfo.InvariantCulture);
            }
            finally
            {
                _isUpdatingColorEffectsNumericInputs = false;
            }

            _isFreezeFrameActive = _isCapturing && _currentColorEffectsSettings.FreezeFrame;
            UpdateUIState();

            if (_isFreezeFrameActive)
            {
                ReprocessAndRefreshFrozenFrame();
            }
            else
            {
                UpdatePreviewIfAvailable();
            }

            TrySaveCurrentSettings();
        }

        private static bool TryParseHexColor(string? hex, out byte r, out byte g, out byte b)
        {
            r = g = b = 0;
            var value = hex?.Trim();
            if (string.IsNullOrWhiteSpace(value))
                return false;

            if (value.StartsWith("#", StringComparison.Ordinal))
                value = value.Substring(1);

            if (value.Length != 6)
                return false;

            if (!byte.TryParse(value.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r))
                return false;
            if (!byte.TryParse(value.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g))
                return false;
            if (!byte.TryParse(value.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b))
                return false;

            return true;
        }

        private static bool TryParseRgba(string? text, out byte r, out byte g, out byte b, out byte a)
        {
            r = g = b = 0;
            a = 255;
            var value = text?.Trim();
            if (string.IsNullOrWhiteSpace(value))
                return false;

            var parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || parts.Length > 4)
                return false;

            if (!byte.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out r))
                return false;
            if (!byte.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out g))
                return false;
            if (!byte.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out b))
                return false;

            if (parts.Length == 4 && !byte.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out a))
                return false;

            return true;
        }

        private static bool TryParseHsv(string? text, out double h, out double s, out double v)
        {
            h = s = v = 0;
            var value = text?.Trim();
            if (string.IsNullOrWhiteSpace(value))
                return false;

            var parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3)
                return false;

            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out h))
                return false;
            if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out s))
                return false;
            if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                return false;

            h = Math.Clamp(h, 0, 360);
            s = Math.Clamp(s, 0, 100);
            v = Math.Clamp(v, 0, 100);
            return true;
        }

        private static void HsvToRgb(double h, double sPct, double vPct, out byte r, out byte g, out byte b)
        {
            var s = sPct / 100.0;
            var v = vPct / 100.0;
            var c = v * s;
            var x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
            var m = v - c;

            double rf, gf, bf;
            if (h < 60) { rf = c; gf = x; bf = 0; }
            else if (h < 120) { rf = x; gf = c; bf = 0; }
            else if (h < 180) { rf = 0; gf = c; bf = x; }
            else if (h < 240) { rf = 0; gf = x; bf = c; }
            else if (h < 300) { rf = x; gf = 0; bf = c; }
            else { rf = c; gf = 0; bf = x; }

            r = (byte)Math.Clamp((int)Math.Round((rf + m) * 255), 0, 255);
            g = (byte)Math.Clamp((int)Math.Round((gf + m) * 255), 0, 255);
            b = (byte)Math.Clamp((int)Math.Round((bf + m) * 255), 0, 255);
        }

        private void UpdateColorInputsFromSelectedColor(System.Drawing.Color color)
        {
            _isUpdatingColorInputs = true;
            try
            {
                var hexInput = FindName("ColorHexInput") as TextBox;
                var rgbaInput = FindName("ColorRgbaInput") as TextBox;
                var hsvInput = FindName("ColorHsvInput") as TextBox;

                if (hexInput != null)
                    hexInput.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
                if (rgbaInput != null)
                    rgbaInput.Text = $"{color.R},{color.G},{color.B},{color.A}";

                var hsv = ToHsv(color);
                if (hsvInput != null)
                    hsvInput.Text = $"{hsv.H:F1},{hsv.S:F1},{hsv.V:F1}";
            }
            finally
            {
                _isUpdatingColorInputs = false;
            }
        }

        private void ApplyManualSelectedColor(byte r, byte g, byte b, byte a)
        {
            var color = System.Drawing.Color.FromArgb(a, r, g, b);
            _selectedColor = color;
            _currentColorEffectsSettings.BaseR = r;
            _currentColorEffectsSettings.BaseG = g;
            _currentColorEffectsSettings.BaseB = b;

            UpdateSelectedColorSwatch(color);
            UpdateColorReadout(color, _lastSamplePoint.HasValue ? (int)_lastSamplePoint.Value.X : 0, _lastSamplePoint.HasValue ? (int)_lastSamplePoint.Value.Y : 0);
            UpdateColorInputsFromSelectedColor(color);
            UpdateMagnifierToSolidColor(color);

            if (_isFreezeFrameActive)
                ReprocessAndRefreshFrozenFrame();
            else
                UpdatePreviewIfAvailable();

            TrySaveCurrentSettings();
        }

        private void ColorHexInput_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingColorInputs)
                return;

            var input = sender as TextBox;
            if (input == null)
                return;

            if (TryParseHexColor(input.Text, out var r, out var g, out var b))
                ApplyManualSelectedColor(r, g, b, 255);
            else if (_selectedColor.HasValue)
                UpdateColorInputsFromSelectedColor(_selectedColor.Value);
        }

        private void ColorRgbaInput_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingColorInputs)
                return;

            var input = sender as TextBox;
            if (input == null)
                return;

            if (TryParseRgba(input.Text, out var r, out var g, out var b, out var a))
                ApplyManualSelectedColor(r, g, b, a);
            else if (_selectedColor.HasValue)
                UpdateColorInputsFromSelectedColor(_selectedColor.Value);
        }

        private void ColorHsvInput_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingColorInputs)
                return;

            var input = sender as TextBox;
            if (input == null)
                return;

            if (TryParseHsv(input.Text, out var h, out var s, out var v))
            {
                HsvToRgb(h, s, v, out var r, out var g, out var b);
                ApplyManualSelectedColor(r, g, b, 255);
            }
            else if (_selectedColor.HasValue)
            {
                UpdateColorInputsFromSelectedColor(_selectedColor.Value);
            }
        }

        private void ColorInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                if (sender is TextBox tb)
                    MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                e.Handled = true;
            }
        }

        private void ColorEffectsNumericInput_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingColorEffectsNumericInputs)
                return;

            ApplyColorEffectsNumericInputs();
        }

        private void ColorEffectsNumericInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
                return;

            ApplyColorEffectsNumericInputs();
            MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            e.Handled = true;
        }

        private void ApplyColorEffectsNumericInputs()
        {
            var hueSlider = FindName("ColorEffectsHueToleranceSlider") as Slider;
            var satSlider = FindName("ColorEffectsSaturationToleranceSlider") as Slider;
            var valSlider = FindName("ColorEffectsValueToleranceSlider") as Slider;
            var strengthSlider = FindName("ColorEffectsStrengthSlider") as Slider;
            var hueInput = FindName("ColorEffectsHueToleranceInput") as TextBox;
            var satInput = FindName("ColorEffectsSaturationToleranceInput") as TextBox;
            var valInput = FindName("ColorEffectsValueToleranceInput") as TextBox;
            var strengthInput = FindName("ColorEffectsStrengthInput") as TextBox;

            if (hueSlider == null || satSlider == null || valSlider == null || strengthSlider == null ||
                hueInput == null || satInput == null || valInput == null || strengthInput == null)
                return;

            _isUpdatingColorEffectsNumericInputs = true;
            try
            {
                if (double.TryParse(hueInput.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var hue))
                    hueSlider.Value = Math.Clamp(hue, hueSlider.Minimum, hueSlider.Maximum);
                else
                    hueInput.Text = hueSlider.Value.ToString("F0", CultureInfo.InvariantCulture);

                if (double.TryParse(satInput.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var sat))
                    satSlider.Value = Math.Clamp(sat, satSlider.Minimum, satSlider.Maximum);
                else
                    satInput.Text = satSlider.Value.ToString("F2", CultureInfo.InvariantCulture);

                if (double.TryParse(valInput.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var val))
                    valSlider.Value = Math.Clamp(val, valSlider.Minimum, valSlider.Maximum);
                else
                    valInput.Text = valSlider.Value.ToString("F2", CultureInfo.InvariantCulture);

                if (double.TryParse(strengthInput.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var strength))
                    strengthSlider.Value = Math.Clamp(strength, strengthSlider.Minimum, strengthSlider.Maximum);
                else
                    strengthInput.Text = strengthSlider.Value.ToString("F2", CultureInfo.InvariantCulture);
            }
            finally
            {
                _isUpdatingColorEffectsNumericInputs = false;
            }

            UpdateColorEffectsSettingsFromUI();
        }

        private void UpdatePreviewIfAvailable()
        {
            // Safely attempt to get and clone frames without holding lock
            // (called from UI thread only, not from preview loop)
            Bitmap? processedCopy = null;

            try
            {
                if (_latestProcessedFrame != null)
                    processedCopy = (Bitmap)_latestProcessedFrame.Clone();

                if (processedCopy != null)
                    UpdatePreview(processedCopy);
                else
                {
                    processedCopy?.Dispose();
                }
            }
            catch
            {
                // If frames are disposed during access, silently skip update
                processedCopy?.Dispose();
            }
        }

        private void ReprocessAndRefreshFrozenFrame()
        {
            if (_latestRawCapturedFrame == null)
                return;

            Bitmap? previewCopy = null;
            try
            {
                using var rawClone = (Bitmap)_latestRawCapturedFrame.Clone();
                var colorFxSnapshot = _currentColorEffectsSettings.Clone();
                var postSnapshot = _currentPostProcessingSettings.Clone();

                using var colorApplied = ColorEffectsProcessor.Apply(rawClone, colorFxSnapshot);
                using var processed = ImageProcessor.Process(colorApplied, postSnapshot);

                _latestProcessedFrame?.Dispose();
                _latestProcessedFrame = (Bitmap)processed.Clone();

                previewCopy = (Bitmap)_latestProcessedFrame.Clone();
            }
            catch
            {
                previewCopy?.Dispose();
                return;
            }

            if (previewCopy != null)
                UpdatePreview(previewCopy);
        }

        private void UpdatePreview(Bitmap processedFrame)
        {
            if (processedFrame == null)
                return;

            try
            {
                var processedControl = FindName("ProcessedPreviewImage") as WPFImage;
                if (processedControl == null)
                {
                    processedFrame?.Dispose();
                    return;
                }

                try
                {
                    var processedImage = BitmapToBitmapImage(processedFrame);
                    processedControl.Source = processedImage;

                    // Update process info
                    UpdateProcessInfoLabels();
                }
                finally
                {
                    processedFrame?.Dispose();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in UpdatePreview: {ex}");
                processedFrame?.Dispose();
            }
        }

        private void UpdateProcessInfoLabels()
        {
            if (_activeHandle == IntPtr.Zero)
                return;

            _currentProcessInfo = ProcessWindowInfoProvider.GetWindowInfo(_activeHandle);
            if (_currentProcessInfo == null)
                return;

            try
            {
                Dispatcher.BeginInvoke(() =>
                {
                    var pidLabel = FindName("ProcessIdLabel") as TextBlock;
                    if (pidLabel != null)
                        pidLabel.Text = _currentProcessInfo.ProcessId.ToString();

                    var windowDimLabel = FindName("WindowDimensionsLabel") as TextBlock;
                    if (windowDimLabel != null)
                        windowDimLabel.Text = $"{_currentProcessInfo.WindowWidth}x{_currentProcessInfo.WindowHeight}";

                    var windowPosLabel = FindName("WindowPositionLabel") as TextBlock;
                    if (windowPosLabel != null)
                        windowPosLabel.Text = $"({_currentProcessInfo.WindowX}, {_currentProcessInfo.WindowY})";

                    var clientDimLabel = FindName("ClientDimensionsLabel") as TextBlock;
                    if (clientDimLabel != null)
                        clientDimLabel.Text = $"{_currentProcessInfo.ClientWidth}x{_currentProcessInfo.ClientHeight}";
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error updating process info labels: {ex}");
            }
        }

        private BitmapImage BitmapToBitmapImage(DrawingBitmap bitmap)
        {
            // Use BMP format - uncompressed, fastest for UI updates
            using var memory = new System.IO.MemoryStream();
            bitmap.Save(memory, System.Drawing.Imaging.ImageFormat.Bmp);
            memory.Position = 0;

            var bitmapImage = new BitmapImage();
            bitmapImage.BeginInit();
            bitmapImage.StreamSource = memory;
            bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
            bitmapImage.EndInit();
            bitmapImage.Freeze();

            return bitmapImage;
        }

        private void SetEyedropperUiState(string stateText, System.Windows.Media.Brush stateBrush)
        {
            var stateLabel = FindName("EyedropperStateLabel") as TextBlock;
            if (stateLabel != null)
            {
                stateLabel.Text = stateText;
                stateLabel.Foreground = stateBrush;
            }

            var eyedropperButton = FindName("EyedropperButton") as Button;
            if (eyedropperButton != null)
            {
                eyedropperButton.Content = _isEyedropperActive ? "Stop Eyedropper" : "Start Eyedropper";
                eyedropperButton.Background = _isEyedropperActive ? WPFBrushes.OrangeRed : WPFBrushes.SlateBlue;
            }
        }

        private static (double H, double S, double V) ToHsv(System.Drawing.Color color)
        {
            var h = color.GetHue();
            var s = color.GetSaturation() * 100.0;
            var v = color.GetBrightness() * 100.0;
            return (h, s, v);
        }

        private bool TryMapPreviewPointToBitmapPoint(System.Windows.Point previewPoint, out int x, out int y)
        {
            x = 0;
            y = 0;

            var image = FindName("ProcessedPreviewImage") as WPFImage;
            if (image?.Source is not BitmapSource bitmapSource)
                return false;

            var controlWidth = image.ActualWidth;
            var controlHeight = image.ActualHeight;
            if (controlWidth <= 0 || controlHeight <= 0)
                return false;

            var bmpWidth = bitmapSource.PixelWidth;
            var bmpHeight = bitmapSource.PixelHeight;
            if (bmpWidth <= 0 || bmpHeight <= 0)
                return false;

            var scale = Math.Min(controlWidth / bmpWidth, controlHeight / bmpHeight);
            var renderedWidth = bmpWidth * scale;
            var renderedHeight = bmpHeight * scale;
            var offsetX = (controlWidth - renderedWidth) / 2.0;
            var offsetY = (controlHeight - renderedHeight) / 2.0;

            var px = previewPoint.X - offsetX;
            var py = previewPoint.Y - offsetY;
            if (px < 0 || py < 0 || px >= renderedWidth || py >= renderedHeight)
                return false;

            x = Math.Clamp((int)(px / scale), 0, bmpWidth - 1);
            y = Math.Clamp((int)(py / scale), 0, bmpHeight - 1);
            return true;
        }

        private static Bitmap BuildMagnifierBitmap(Bitmap source, int centerX, int centerY)
        {
            var sampleSize = (MagnifierSampleRadius * 2) + 1;
            var magnifierSize = sampleSize * MagnifierScale;
            var output = new Bitmap(magnifierSize, magnifierSize, source.PixelFormat);

            using var g = Graphics.FromImage(output);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            g.Clear(System.Drawing.Color.Black);

            var srcX = Math.Clamp(centerX - MagnifierSampleRadius, 0, Math.Max(0, source.Width - sampleSize));
            var srcY = Math.Clamp(centerY - MagnifierSampleRadius, 0, Math.Max(0, source.Height - sampleSize));

            g.DrawImage(
                source,
                new System.Drawing.Rectangle(0, 0, magnifierSize, magnifierSize),
                new System.Drawing.Rectangle(srcX, srcY, sampleSize, sampleSize),
                GraphicsUnit.Pixel);

            using var pen = new System.Drawing.Pen(System.Drawing.Color.Red, 1);
            var center = MagnifierSampleRadius * MagnifierScale;
            g.DrawRectangle(pen, center, center, MagnifierScale, MagnifierScale);

            return output;
        }

        private void UpdateColorReadout(System.Drawing.Color color, int x, int y)
        {
            _lastSamplePoint = new System.Windows.Point(x, y);

            var hexLabel = FindName("ColorHexLabel") as TextBlock;
            var rgbaLabel = FindName("ColorRgbaLabel") as TextBlock;
            var hsvLabel = FindName("ColorHsvLabel") as TextBlock;
            var coordLabel = FindName("ColorCoordLabel") as TextBlock;

            if (hexLabel != null)
                hexLabel.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";

            if (rgbaLabel != null)
                rgbaLabel.Text = $"{color.R}, {color.G}, {color.B}, {color.A}";

            var hsv = ToHsv(color);
            if (hsvLabel != null)
                hsvLabel.Text = $"{hsv.H:F1}°, {hsv.S:F1}%, {hsv.V:F1}%";

            if (coordLabel != null)
                coordLabel.Text = _lastSamplePoint.HasValue || (x != 0 || y != 0) ? $"({x}, {y})" : "-";
        }

        private void UpdateSelectedColorSwatch(System.Drawing.Color color)
        {
            var swatch = FindName("SelectedColorSwatch") as System.Windows.Shapes.Rectangle;
            if (swatch != null)
            {
                swatch.Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(color.A, color.R, color.G, color.B));
            }
        }

        private void UpdateMagnifierToSolidColor(System.Drawing.Color color)
        {
            var tileSize = 16;
            using var solid = new Bitmap(tileSize, tileSize, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(solid))
            {
                g.Clear(System.Drawing.Color.FromArgb(color.A, color.R, color.G, color.B));
            }

            using var magnifier = BuildMagnifierBitmap(solid, tileSize / 2, tileSize / 2);
            var magnifierBitmap = BitmapToBitmapImage(magnifier);

            var magnifierImage = FindName("ColorMagnifierImage") as WPFImage;
            if (magnifierImage != null)
                magnifierImage.Source = magnifierBitmap;

            var floatingMagnifierImage = FindName("FloatingMagnifierImage") as WPFImage;
            if (floatingMagnifierImage != null)
                floatingMagnifierImage.Source = magnifierBitmap;
        }

        private bool TrySampleColorAtScreenPoint(int screenX, int screenY, out System.Drawing.Color color, out int x, out int y)
        {
            color = System.Drawing.Color.Transparent;
            x = screenX;
            y = screenY;

            var sampleSize = (MagnifierSampleRadius * 2) + 1;
            var srcX = screenX - MagnifierSampleRadius;
            var srcY = screenY - MagnifierSampleRadius;

            using var sample = new Bitmap(sampleSize, sampleSize, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(sample))
            {
                g.CopyFromScreen(srcX, srcY, 0, 0, new System.Drawing.Size(sampleSize, sampleSize), CopyPixelOperation.SourceCopy);
            }

            var center = MagnifierSampleRadius;
            color = sample.GetPixel(center, center);

            using var magnifier = BuildMagnifierBitmap(sample, center, center);
            var magnifierBitmap = BitmapToBitmapImage(magnifier);

            var magnifierImage = FindName("ColorMagnifierImage") as WPFImage;
            if (magnifierImage != null)
                magnifierImage.Source = magnifierBitmap;

            var floatingMagnifierImage = FindName("FloatingMagnifierImage") as WPFImage;
            if (floatingMagnifierImage != null)
                floatingMagnifierImage.Source = magnifierBitmap;

            return true;
        }

        private void UpdateFloatingMagnifierPosition(int screenX, int screenY)
        {
            var popup = FindName("FloatingMagnifierPopup") as System.Windows.Controls.Primitives.Popup;
            if (popup == null)
                return;

            const int offsetX = 18;
            const int offsetY = 18;
            popup.HorizontalOffset = screenX + offsetX;
            popup.VerticalOffset = screenY + offsetY;

            if (_isEyedropperActive && _isCapturing)
                popup.IsOpen = true;
        }

        private void OpenEyedropperOverlay()
        {
            if (!Win32Interop.GetWindowRect(_activeHandle, out var rect))
                return;

            if (_eyedropperOverlay != null)
            {
                try { _eyedropperOverlay.Close(); } catch { }
                _eyedropperOverlay = null;
            }

            var overlay = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                AllowsTransparency = true,
                Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(1, 0, 0, 0)),
                Topmost = true,
                Left = rect.Left,
                Top = rect.Top,
                Width = Math.Max(1, rect.Right - rect.Left),
                Height = Math.Max(1, rect.Bottom - rect.Top),
                Cursor = System.Windows.Input.Cursors.Cross,
                Owner = this
            };

            overlay.MouseMove += EyedropperOverlay_MouseMove;
            overlay.MouseLeftButtonDown += EyedropperOverlay_MouseLeftButtonDown;
            overlay.KeyDown += EyedropperOverlay_KeyDown;
            overlay.Deactivated += (_, __) =>
            {
                if (_isEyedropperActive)
                    CancelEyedropperMode();
            };

            _eyedropperOverlay = overlay;
            overlay.Show();
            overlay.Focus();
        }

        private void EyedropperOverlay_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isEyedropperActive)
                return;

            if (!Win32Interop.TryGetCursorPos(out var point))
                return;

            if (TrySampleColorAtScreenPoint(point.X, point.Y, out var sampledColor, out var x, out var y))
            {
                UpdateColorReadout(sampledColor, x, y);
                UpdateFloatingMagnifierPosition(point.X, point.Y);
                SetEyedropperUiState("Sampling target process... Left click to select, Esc to cancel", WPFBrushes.DodgerBlue);
            }
        }

        private void EyedropperOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!_isEyedropperActive)
                return;

            if (!Win32Interop.TryGetCursorPos(out var point))
                return;

            if (TrySampleColorAtScreenPoint(point.X, point.Y, out var selected, out var sx, out var sy))
            {
                _selectedColor = selected;
                UpdateSelectedColorSwatch(selected);
                UpdateColorReadout(selected, sx, sy);
                UpdateColorInputsFromSelectedColor(selected);

                // Sync picked color as color-effects base color
                _currentColorEffectsSettings.BaseR = selected.R;
                _currentColorEffectsSettings.BaseG = selected.G;
                _currentColorEffectsSettings.BaseB = selected.B;

                // Reflect color effects UI state immediately
                var targetHexTextBox = FindName("ColorEffectsTargetHexTextBox") as TextBox;
                if (targetHexTextBox != null && string.IsNullOrWhiteSpace(targetHexTextBox.Text))
                    targetHexTextBox.Text = $"#{selected.R:X2}{selected.G:X2}{selected.B:X2}";

                // Gracefully stop eyedropper and remove overlay/popup.
                CancelEyedropperMode();
                SetEyedropperUiState("Selected", WPFBrushes.DarkGreen);
                UpdatePreviewIfAvailable();
                TrySaveCurrentSettings();
            }
        }

        private void EyedropperOverlay_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && _isEyedropperActive)
            {
                CancelEyedropperMode();
                e.Handled = true;
            }
        }

        private void ColorCoordLabel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_lastSamplePoint == null)
                return;

            var x = (int)_lastSamplePoint.Value.X;
            var y = (int)_lastSamplePoint.Value.Y;
            Win32Interop.SetCursorPos(x, y);
            e.Handled = true;
        }

        private void PreviewBorder_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isPanning && e.MiddleButton == MouseButtonState.Pressed && _zoomLevel > 1.0)
            {
                var current = e.GetPosition(PreviewBorder);
                var delta = current - _lastPanPoint;
                _lastPanPoint = current;

                _panX += delta.X;
                _panY += delta.Y;

                UpdateZoomLevel();
                return;
            }

            // Eyedropper sampling is external to this app (target process window).
        }

        private void PreviewBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Eyedropper selection is now external to this app (target process window),
            // so in-preview clicks are intentionally ignored.
        }

        private void UpdateUIState()
        {
            var selectedMode = GetSelectedCaptureMode();
            var hasValidProcess = ProcessComboBox.SelectedItem is ProcessInfo;

            // Start Capture button state (preview streaming gate)
            // Should be disabled while capturing dataset to prevent accidentally stopping the stream
            var startPreviewButton = FindName("StartPreviewButton") as Button;
            if (startPreviewButton != null)
            {
                startPreviewButton.Content = _isCapturing ? "Stop Capture" : "Start Capture";
                startPreviewButton.Background = _isCapturing ? WPFBrushes.OrangeRed : WPFBrushes.CornflowerBlue;
                startPreviewButton.IsEnabled = hasValidProcess && !_isCapturingDataset;
            }

            // Toggle button for dataset screenshot capture
            // Enabled when previewing (to start captures) or when already capturing (to stop captures)
            var modeText = selectedMode == CaptureMode.AutoTimed ? "Auto Screenshots" : "Manual Screenshots";
            ToggleButton.Content = _isCapturingDataset ? $"Stop {modeText}" : $"Start {modeText}";
            ToggleButton.Background = _isCapturingDataset ? WPFBrushes.OrangeRed : WPFBrushes.CornflowerBlue;
            ToggleButton.IsEnabled = _isCapturing;

            // Process selector: only enabled when not previewing
            ProcessComboBox.IsEnabled = !_isCapturing;

            // Settings: only enabled when previewing but not capturing dataset
            DatasetNameTextBox.IsEnabled = _isCapturing && !_isCapturingDataset;
            CaptureModeSelector.IsEnabled = _isCapturing && !_isCapturingDataset;
            IntervalTextBox.IsEnabled = _isCapturing && !_isCapturingDataset && selectedMode == CaptureMode.AutoTimed;
            ManualKeyInput.IsEnabled = _isCapturing && !_isCapturingDataset && selectedMode == CaptureMode.ManualKeystroke;

            // Post-processing controls: only enabled when previewing but not capturing dataset
            SetPostProcessingControlsEnabled(_isCapturing && !_isCapturingDataset);

            // Color effects controls disabled while taking screenshots
            SetColorEffectsControlsEnabled(_isCapturing && !_isCapturingDataset);

            var eyedropperButton = FindName("EyedropperButton") as Button;
            if (eyedropperButton != null)
                eyedropperButton.IsEnabled = _isCapturing && !_isCapturingDataset && !_isFreezeFrameActive;

            var exportButton = FindName("ExportSettingsButton") as Button;
            if (exportButton != null)
                exportButton.IsEnabled = _isCapturing && !_isCapturingDataset && !_isFreezeFrameActive;

            var importButton = FindName("ImportSettingsButton") as Button;
            if (importButton != null)
                importButton.IsEnabled = _isCapturing && !_isCapturingDataset && !_isFreezeFrameActive;

            var resetButton = FindName("ResetSettingsButton") as Button;
            if (resetButton != null)
                resetButton.IsEnabled = _isCapturing && !_isCapturingDataset && !_isFreezeFrameActive;

            var toggleButton = FindName("ToggleButton") as Button;
            if (toggleButton != null)
                toggleButton.IsEnabled = _isCapturing && !_isFreezeFrameActive;

            var startPreviewButton2 = FindName("StartPreviewButton") as Button;
            if (startPreviewButton2 != null)
                startPreviewButton2.IsEnabled = hasValidProcess && !_isCapturingDataset && !_isFreezeFrameActive;

            if (!_isCapturing && _isEyedropperActive)
                CancelEyedropperMode();

            if (!_isCapturing)
            {
                _isFreezeFrameActive = false;
                var popup = FindName("FloatingMagnifierPopup") as System.Windows.Controls.Primitives.Popup;
                if (popup != null)
                    popup.IsOpen = false;

                SetEyedropperUiState("Inactive", WPFBrushes.Gray);
            }

            StateLabel.Text = _isCapturingDataset ? "Capturing Dataset" : (_isCapturing ? "Previewing" : "Stopped");
            CaptureModeStatusLabel.Text = GetCaptureModeDisplayName(selectedMode);

            if (!_isCapturing)
            {
                ProcessLabel.Text = (ProcessComboBox.SelectedItem as ProcessInfo)?.DisplayName ?? "-";
                DatasetLabel.Text = string.IsNullOrWhiteSpace(DatasetNameTextBox.Text) ? "-" : DatasetNameTextBox.Text.Trim();
            }
        }

        private void SetPostProcessingControlsEnabled(bool enabled)
        {
            var groups = new[]
            {
                new[] { "PostProcessingEnabledCheckBox", "GrayscaleCheckBox" },
                new[] { "BrightnessSlider", "ContrastSlider", "SaturationSlider", "GammaSlider" },
                new[] { "BrightnessLabel", "ContrastLabel", "SaturationLabel", "GammaLabel" },
                new[] { "CropEnabledCheckBox", "CropXTextBox", "CropYTextBox", "CropWidthTextBox", "CropHeightTextBox" },
                new[] { "ResizeEnabledCheckBox", "ResizeWidthTextBox", "ResizeHeightTextBox" }
            };

            foreach (var group in groups)
            {
                foreach (var name in group)
                {
                    var control = FindName(name);
                    if (control is FrameworkElement element)
                        element.IsEnabled = enabled;
                }
            }
        }

        private void SetColorEffectsControlsEnabled(bool enabled)
        {
            var groups = new[]
            {
                new[] { "ColorEffectsEnabledCheckBox", "ColorEffectsMaskOverlayCheckBox", "ColorEffectsFreezeFrameCheckBox" },
                new[] { "ColorEffectsHueToleranceSlider", "ColorEffectsSaturationToleranceSlider", "ColorEffectsValueToleranceSlider", "ColorEffectsStrengthSlider" },
                new[] { "ColorEffectsHueToleranceLabel", "ColorEffectsSaturationToleranceLabel", "ColorEffectsValueToleranceLabel", "ColorEffectsStrengthLabel" },
                new[] { "ColorEffectsModeComboBox", "ColorEffectsTargetHexTextBox" }
            };

            foreach (var group in groups)
            {
                foreach (var name in group)
                {
                    var control = FindName(name);
                    if (control is FrameworkElement element)
                        element.IsEnabled = enabled;
                }
            }

            // Freeze frame checkbox must remain usable during freeze so user can unfreeze.
            var freezeCheckbox = FindName("ColorEffectsFreezeFrameCheckBox") as CheckBox;
            if (freezeCheckbox != null && _isCapturing && !_isCapturingDataset)
                freezeCheckbox.IsEnabled = true;
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
            _latestRawCapturedFrame?.Dispose();
            _previewCts?.Cancel();
            _captureCts?.Cancel();
            base.OnClosed(e);
        }

        private void ExportSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new SaveFileDialog
                {
                    Filter = "JSON Settings (*.json)|*.json|All Files (*.*)|*.*",
                    DefaultExt = ".json",
                    FileName = $"capture-settings-{DateTime.Now:yyyyMMdd_HHmmss}.json"
                };

                if (dialog.ShowDialog() == true)
                {
                    var settings = BuildCurrentSettings();
                    AppCaptureSettingsStore.ExportToFile(settings, dialog.FileName);
                    ShowSettingsMessage($"Settings exported to: {Path.GetFileName(dialog.FileName)}", true);
                }
            }
            catch (Exception ex)
            {
                ShowSettingsMessage($"Export failed: {ex.Message}", false);
            }
        }

        private void ImportSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new OpenFileDialog
                {
                    Filter = "JSON Settings (*.json)|*.json|All Files (*.*)|*.*",
                    DefaultExt = ".json"
                };

                if (dialog.ShowDialog() == true)
                {
                    var importedSettings = AppCaptureSettingsStore.ImportFromFile(dialog.FileName);
                    ApplySettings(importedSettings);
                    TrySaveCurrentSettings();
                    ShowSettingsMessage("Settings imported successfully", true);
                }
            }
            catch (Exception ex)
            {
                ShowSettingsMessage($"Import failed: {ex.Message}", false);
            }
        }

        private void ResetSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var result = System.Windows.MessageBox.Show(
                    "Reset all settings to defaults? This cannot be undone.",
                    "Reset Settings",
                    System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Warning);

                if (result == System.Windows.MessageBoxResult.Yes)
                {
                    var defaultSettings = new AppCaptureSettings();
                    ApplySettings(defaultSettings);
                    TrySaveCurrentSettings();
                    ShowSettingsMessage("Settings reset to defaults", true);
                }
            }
            catch (Exception ex)
            {
                ShowSettingsMessage($"Reset failed: {ex.Message}", false);
            }
        }

        private void ShowSettingsMessage(string message, bool isSuccess)
        {
            var msgBlock = FindName("SettingsMessageBlock") as TextBlock;
            if (msgBlock != null)
            {
                msgBlock.Text = message;
                msgBlock.Foreground = isSuccess ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.DarkGreen) : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Firebrick);
            }
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
