using System.Globalization;
using System.Drawing;
using DrawingBitmap = System.Drawing.Bitmap;
using System.Windows;
using System.Windows.Controls;
using WPFImage = System.Windows.Controls.Image;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WPFBrushes = System.Windows.Media.Brushes;

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
        private Task? _previewLoopTask;
        private GlobalKeyboardHook? _keyboardHook;
        private bool _isCapturing;
        private CaptureMode _activeCaptureMode = CaptureMode.AutoTimed;
        private IntPtr _activeHandle;
        private string _activeDataset = "";
        private int _nextImageNumber;
        private int _captureCount;

        private Bitmap? _latestRawFrame;
        private Bitmap? _latestProcessedFrame;
        private PostProcessingSettings _currentPostProcessingSettings = new();

        private ComboBox CaptureModeSelector => (ComboBox)FindName("CaptureModeComboBox");
        private StackPanel IntervalSettingsPanel => (StackPanel)FindName("IntervalPanel");
        private StackPanel ManualKeySettingsPanel => (StackPanel)FindName("ManualKeyPanel");
        private TextBox ManualKeyInput => (TextBox)FindName("ManualKeyTextBox");
        private TextBlock CaptureModeStatusLabel => (TextBlock)FindName("ModeLabel");

        public MainWindow()
        {
            InitializeComponent();
            ConfigureCaptureModes();
            ConfigurePostProcessingUI();
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
                PostProcessing = settings.PostProcessing ?? new PostProcessingSettings()
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
                await StopCaptureAsync();
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
            var cts = new CancellationTokenSource();
            _cts = cts;
            _previewLoopTask = Task.Run(() => PreviewLoopAsync(cts));
            UpdateUIState();
        }        private void CaptureModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
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

            // Start preview loop (continuous preview capture at ~30fps)
            _previewLoopTask = Task.Run(() => PreviewLoopAsync(cts));
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
            var previewLoop = _previewLoopTask;
            if (loop != null)
                await loop;
            if (previewLoop != null)
                await previewLoop;
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

        private async Task PreviewLoopAsync(CancellationTokenSource cts)
        {
            var token = cts.Token;

            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(33)); // ~30fps
                do
                {
                    bool lockTaken = false;
                    try
                    {
                        lockTaken = await _captureLock.WaitAsync(0, token);
                        if (!lockTaken)
                            continue;

                        using var bitmap = ScreenshotCapture.CaptureWindow(_activeHandle);
                        if (bitmap != null)
                        {
                            _latestRawFrame?.Dispose();
                            _latestRawFrame = (DrawingBitmap)bitmap.Clone();

                            var settingsSnapshot = _currentPostProcessingSettings.Clone();
                            using var processed = ImageProcessor.Process(bitmap, settingsSnapshot);

                            _latestProcessedFrame?.Dispose();
                            _latestProcessedFrame = (DrawingBitmap)processed.Clone();

                            // Make safe copies while holding the lock
                            var rawCopy = (Bitmap)_latestRawFrame.Clone();
                            var processedCopy = (Bitmap)_latestProcessedFrame.Clone();

                            _ = Dispatcher.BeginInvoke(() =>
                            {
                                if (ReferenceEquals(_cts, cts))
                                {
                                    UpdatePreview(rawCopy, processedCopy);
                                }
                            });
                        }
                    }
                    finally
                    {
                        if (lockTaken)
                            _captureLock.Release();
                    }
                }
                while (await timer.WaitForNextTickAsync(token));
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

                using var rawBitmap = ScreenshotCapture.CaptureWindow(_activeHandle);
                if (rawBitmap == null)
                {
                    await HandleCaptureFailureAsync(cts, "Failed to capture screenshot (window closed, minimized or invalid).");
                    return false;
                }

                token.ThrowIfCancellationRequested();

                // Update raw frame for preview
                _latestRawFrame?.Dispose();
                _latestRawFrame = (Bitmap)rawBitmap.Clone();

                // Process the frame using current settings
                var settingsSnapshot = _currentPostProcessingSettings.Clone();
                using var processedBitmap = ImageProcessor.Process(rawBitmap, settingsSnapshot);

                token.ThrowIfCancellationRequested();

                // Update processed frame for preview
                _latestProcessedFrame?.Dispose();
                _latestProcessedFrame = (Bitmap)processedBitmap.Clone();

                // Save the processed bitmap to disk
                var filename = DatasetWriter.SaveScreenshot(_activeDataset, _nextImageNumber++, processedBitmap);
                _captureCount++;
                var saved = _captureCount;

                // Make safe copies for preview (while holding lock)
                var rawCopy = (Bitmap)rawBitmap.Clone();
                var processedCopy = (Bitmap)processedBitmap.Clone();

                // Update UI
                _ = Dispatcher.BeginInvoke(() =>
                {
                    if (ReferenceEquals(_cts, cts))
                    {
                        CapturedLabel.Text = saved.ToString(CultureInfo.InvariantCulture);
                        LastFileLabel.Text = filename;
                        UpdatePreview(rawCopy, processedCopy); // Update preview with new frame
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
                    : "K",
                PostProcessing = _currentPostProcessingSettings.Clone()
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

            UpdatePreviewIfAvailable();
            TrySaveCurrentSettings();
        }

        private void UpdatePreviewIfAvailable()
        {
            // Safely attempt to get and clone frames without holding lock
            // (called from UI thread only, not from preview loop)
            Bitmap? rawCopy = null;
            Bitmap? processedCopy = null;

            try
            {
                if (_latestRawFrame != null)
                    rawCopy = (Bitmap)_latestRawFrame.Clone();
                if (_latestProcessedFrame != null)
                    processedCopy = (Bitmap)_latestProcessedFrame.Clone();
                else if (rawCopy != null)
                    processedCopy = (Bitmap)rawCopy.Clone();

                if (rawCopy != null && processedCopy != null)
                    UpdatePreview(rawCopy, processedCopy);
                else
                {
                    rawCopy?.Dispose();
                    processedCopy?.Dispose();
                }
            }
            catch
            {
                // If frames are disposed during access, silently skip update
                rawCopy?.Dispose();
                processedCopy?.Dispose();
            }
        }

        private void UpdatePreview(Bitmap rawFrame, Bitmap processedFrame)
        {
            var processedControl = FindName("ProcessedPreviewImage") as WPFImage;
            var originalControl = FindName("OriginalPreviewImage") as WPFImage;
            if (processedControl == null || originalControl == null)
            {
                rawFrame?.Dispose();
                processedFrame?.Dispose();
                return;
            }

            try
            {
                var processedImage = BitmapToBitmapImage(processedFrame);
                var originalImage = BitmapToBitmapImage(rawFrame);

                processedControl.Source = processedImage;
                originalControl.Source = originalImage;
            }
            finally
            {
                rawFrame?.Dispose();
                processedFrame?.Dispose();
            }
        }

        private BitmapImage BitmapToBitmapImage(DrawingBitmap bitmap)
        {
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

        private void UpdateUIState()
        {
            var selectedMode = GetSelectedCaptureMode();
            var hasValidProcess = ProcessComboBox.SelectedItem is ProcessInfo;

            // Start Preview button state
            var startPreviewButton = FindName("StartPreviewButton") as Button;
            if (startPreviewButton != null)
            {
                startPreviewButton.Content = _isCapturing ? "Stop Capture" : "Start Capture";
                startPreviewButton.Background = _isCapturing ? WPFBrushes.OrangeRed : WPFBrushes.CornflowerBlue;
                startPreviewButton.IsEnabled = hasValidProcess;
            }

            // Toggle button for dataset capture (only enabled when previewing)
            ToggleButton.Content = "Start Capture";
            ToggleButton.Background = WPFBrushes.CornflowerBlue;
            ToggleButton.IsEnabled = _isCapturing;

            // Process selector: only enabled when not previewing
            ProcessComboBox.IsEnabled = !_isCapturing;

            // Settings: only enabled when previewing
            DatasetNameTextBox.IsEnabled = _isCapturing;
            CaptureModeSelector.IsEnabled = _isCapturing;
            IntervalTextBox.IsEnabled = _isCapturing && selectedMode == CaptureMode.AutoTimed;
            ManualKeyInput.IsEnabled = _isCapturing && selectedMode == CaptureMode.ManualKeystroke;

            // Post-processing controls: only enabled when previewing
            SetPostProcessingControlsEnabled(_isCapturing);

            StateLabel.Text = _isCapturing ? "Previewing" : "Stopped";
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
