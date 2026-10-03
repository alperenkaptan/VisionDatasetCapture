using System.Windows;
using System.Windows.Media;

namespace VisionDatasetCapture
{
    public partial class MainWindow : Window
    {
        private CancellationTokenSource? _cts;
        private Task? _loopTask;
        private bool _isCapturing;

        public MainWindow()
        {
            InitializeComponent();
            RefreshProcessList(null);
            UpdateUIState();
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
        }

        private string ValidateInputs(out int interval)
        {
            interval = 0;

            if (ProcessComboBox.SelectedItem == null)
                return "Please select a process.";

            var datasetName = DatasetNameTextBox.Text?.Trim() ?? "";
            if (datasetName.Length == 0)
                return "Dataset/class name cannot be empty.";

            if (!DatasetWriter.IsValidDatasetName(datasetName))
                return "Dataset name contains invalid characters.";

            if (!int.TryParse(IntervalTextBox.Text?.Trim(), out interval) || interval <= 0)
                return "Screenshot interval must be a positive integer.";

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

            var error = ValidateInputs(out var interval);
            if (error.Length > 0)
            {
                ErrorMessageBlock.Text = error;
                return;
            }

            var selected = (ProcessInfo)ProcessComboBox.SelectedItem;
            var dataset = DatasetNameTextBox.Text.Trim();

            // Resolve the window handle once; it is reused for every frame.
            var handle = ProcessSelector.GetWindowHandleForProcessId(selected.ProcessId);
            if (handle == IntPtr.Zero)
                handle = selected.WindowHandle;
            if (handle == IntPtr.Zero)
            {
                ErrorMessageBlock.Text = "Selected process window is no longer available.";
                return;
            }

            int nextNumber;
            try
            {
                DatasetWriter.EnsureDatasetFolderExists(dataset);
                nextNumber = DatasetWriter.GetNextImageNumber(dataset);
            }
            catch (Exception ex)
            {
                ErrorMessageBlock.Text = $"Failed to prepare dataset folder: {ex.Message}";
                return;
            }

            ProcessLabel.Text = selected.DisplayName;
            DatasetLabel.Text = dataset;
            CapturedLabel.Text = "0";
            LastFileLabel.Text = "-";

            var cts = new CancellationTokenSource();
            _cts = cts;
            _isCapturing = true;
            UpdateUIState();

            _loopTask = Task.Run(() => CaptureLoopAsync(handle, dataset, nextNumber, interval, cts));
        }

        private async Task StopCaptureAsync()
        {
            _isCapturing = false;
            var cts = _cts;
            _cts = null;
            cts?.Cancel();

            // Wait for the in-flight capture to finish so a quick restart cannot reuse its file numbers.
            ToggleButton.IsEnabled = false;
            var loop = _loopTask;
            if (loop != null)
                await loop;
            ToggleButton.IsEnabled = true;
            UpdateUIState();
        }

        private async Task CaptureLoopAsync(IntPtr handle, string dataset, int number, int intervalSeconds, CancellationTokenSource cts)
        {
            var token = cts.Token;
            var count = 0;
            string? failure = null;

            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));
                do
                {
                    using var bitmap = ScreenshotCapture.CaptureWindow(handle);
                    if (bitmap == null)
                    {
                        failure = "Failed to capture screenshot (window closed, minimized or invalid).";
                        break;
                    }

                    token.ThrowIfCancellationRequested();
                    var filename = DatasetWriter.SaveScreenshot(dataset, number++, bitmap);
                    count++;

                    var saved = count;
                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        if (ReferenceEquals(_cts, cts))
                        {
                            CapturedLabel.Text = saved.ToString();
                            LastFileLabel.Text = filename;
                        }
                    });
                }
                while (await timer.WaitForNextTickAsync(token));
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                failure = $"Capture error: {ex.Message}";
            }

            if (failure != null)
            {
                _ = Dispatcher.BeginInvoke(() =>
                {
                    if (ReferenceEquals(_cts, cts))
                    {
                        ErrorMessageBlock.Text = failure;
                        _ = StopCaptureAsync();
                    }
                });
            }
        }

        private void UpdateUIState()
        {
            ToggleButton.Content = _isCapturing ? "Stop" : "Start";
            ToggleButton.Background = _isCapturing ? Brushes.OrangeRed : Brushes.CornflowerBlue;
            ProcessComboBox.IsEnabled = !_isCapturing;
            DatasetNameTextBox.IsEnabled = !_isCapturing;
            IntervalTextBox.IsEnabled = !_isCapturing;
            StateLabel.Text = _isCapturing ? "Capturing" : "Stopped";
        }

        protected override void OnClosed(EventArgs e)
        {
            _cts?.Cancel();
            base.OnClosed(e);
        }
    }
}
