using System.IO;
using System.Text.Json;

namespace VisionDatasetCapture
{
    public enum CaptureMode
    {
        AutoTimed,
        ManualKeystroke
    }

    public class AppCaptureSettings
    {
        public int? SelectedProcessId { get; set; }
        public string DatasetName { get; set; } = "";
        public CaptureMode CaptureMode { get; set; } = CaptureMode.AutoTimed;
        public int IntervalSeconds { get; set; } = 1;
        public string ManualKey { get; set; } = "K";
    }

    public static class AppCaptureSettingsStore
    {
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            WriteIndented = true
        };

        public static string SettingsFilePath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "capture-settings.json");

        public static AppCaptureSettings LoadOrDefault()
        {
            try
            {
                if (!File.Exists(SettingsFilePath))
                    return new AppCaptureSettings();

                using var stream = File.OpenRead(SettingsFilePath);
                var settings = JsonSerializer.Deserialize<AppCaptureSettings>(stream);
                return settings ?? new AppCaptureSettings();
            }
            catch
            {
                return new AppCaptureSettings();
            }
        }

        public static void Save(AppCaptureSettings settings)
        {
            var directory = Path.GetDirectoryName(SettingsFilePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(settings, SerializerOptions);
            File.WriteAllText(SettingsFilePath, json);
        }
    }
}
