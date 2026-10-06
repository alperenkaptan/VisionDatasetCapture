using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VisionDatasetCapture
{
    public enum CaptureMode
    {
        AutoTimed,
        ManualKeystroke
    }

    public class PostProcessingCropSettings
    {
        public bool Enabled { get; set; } = false;
        public int X { get; set; } = 0;
        public int Y { get; set; } = 0;
        public int Width { get; set; } = 0;
        public int Height { get; set; } = 0;
    }

    public class PostProcessingResizeSettings
    {
        public bool Enabled { get; set; } = false;
        public int Width { get; set; } = 800;
        public int Height { get; set; } = 600;
    }

    public class PostProcessingSettings
    {
        // Processing enabled/disabled
        public bool Enabled { get; set; } = true;

        // Color effects
        public bool Grayscale { get; set; } = false;
        public float Brightness { get; set; } = 0f; // Range: -1 to 1
        public float Contrast { get; set; } = 1f;   // Range: 0.1 to 3.0
        public float Saturation { get; set; } = 1f; // Range: 0 to 2
        public float Gamma { get; set; } = 1f;      // Range: 0.1 to 3.0

        // Spatial transforms
        public PostProcessingCropSettings Crop { get; set; } = new();
        public PostProcessingResizeSettings Resize { get; set; } = new();

        /// <summary>
        /// Processing order: Crop → Brightness → Contrast → Saturation → Gamma → Grayscale → Resize
        /// </summary>
        public PostProcessingSettings Clone()
        {
            return new PostProcessingSettings
            {
                Enabled = Enabled,
                Grayscale = Grayscale,
                Brightness = Brightness,
                Contrast = Contrast,
                Saturation = Saturation,
                Gamma = Gamma,
                Crop = new PostProcessingCropSettings
                {
                    Enabled = Crop.Enabled,
                    X = Crop.X,
                    Y = Crop.Y,
                    Width = Crop.Width,
                    Height = Crop.Height
                },
                Resize = new PostProcessingResizeSettings
                {
                    Enabled = Resize.Enabled,
                    Width = Resize.Width,
                    Height = Resize.Height
                }
            };
        }
    }

    public enum ColorEffectMode
    {
        Highlight,
        ReplaceColor,
        Grayscale,
        Brighten,
        Darken,
        CustomColor
    }

    public class ColorEffectsSettings
    {
        public bool Enabled { get; set; } = false;
        public bool ShowMaskOverlay { get; set; } = false;
        public bool FreezeFrame { get; set; } = false;

        // Base selected color (from eyedropper)
        public byte BaseR { get; set; } = 255;
        public byte BaseG { get; set; } = 255;
        public byte BaseB { get; set; } = 255;

        // HSV tolerances
        public double HueTolerance { get; set; } = 15;         // degrees, 0-180
        public double SaturationTolerance { get; set; } = 0.20; // 0-1
        public double ValueTolerance { get; set; } = 0.20;      // 0-1

        public ColorEffectMode EffectMode { get; set; } = ColorEffectMode.Highlight;
        public double EffectStrength { get; set; } = 0.5;       // 0-1

        // Target/custom effect color
        public byte TargetR { get; set; } = 255;
        public byte TargetG { get; set; } = 255;
        public byte TargetB { get; set; } = 0;

        public ColorEffectsSettings Clone()
        {
            return new ColorEffectsSettings
            {
                Enabled = Enabled,
                ShowMaskOverlay = ShowMaskOverlay,
                FreezeFrame = FreezeFrame,
                BaseR = BaseR,
                BaseG = BaseG,
                BaseB = BaseB,
                HueTolerance = HueTolerance,
                SaturationTolerance = SaturationTolerance,
                ValueTolerance = ValueTolerance,
                EffectMode = EffectMode,
                EffectStrength = EffectStrength,
                TargetR = TargetR,
                TargetG = TargetG,
                TargetB = TargetB
            };
        }
    }

    public class AppCaptureSettings
    {
        /// <summary>
        /// Schema version for forward/backward compatibility during import/export.
        /// </summary>
        [JsonPropertyName("settingsVersion")]
        public int SettingsVersion { get; set; } = 2;

        public int? SelectedProcessId { get; set; }
        public string DatasetName { get; set; } = "";
        public CaptureMode CaptureMode { get; set; } = CaptureMode.AutoTimed;
        public int IntervalSeconds { get; set; } = 1;
        public string ManualKey { get; set; } = "K";
        public PostProcessingSettings PostProcessing { get; set; } = new();
        public ColorEffectsSettings ColorEffects { get; set; } = new();

        /// <summary>
        /// Preview zoom level (1.0 = 100% / no zoom).
        /// </summary>
        public double ZoomLevel { get; set; } = 1.0;
    }

    public static class AppCaptureSettingsStore
    {
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault
        };

        public static string SettingsFilePath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "capture-settings.json");

        public static AppCaptureSettings LoadOrDefault()
        {
            try
            {
                if (!File.Exists(SettingsFilePath))
                    return new AppCaptureSettings();

                using var stream = File.OpenRead(SettingsFilePath);
                var settings = JsonSerializer.Deserialize<AppCaptureSettings>(stream, SerializerOptions);
                return EnsureDefaults(settings ?? new AppCaptureSettings());
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

        /// <summary>
        /// Exports current settings to a specified JSON file.
        /// </summary>
        public static string ExportToJson(AppCaptureSettings settings)
        {
            return JsonSerializer.Serialize(settings, SerializerOptions);
        }

        /// <summary>
        /// Imports settings from JSON string, tolerating missing/unknown fields.
        /// </summary>
        public static AppCaptureSettings ImportFromJson(string json)
        {
            try
            {
                var settings = JsonSerializer.Deserialize<AppCaptureSettings>(json, SerializerOptions);
                return EnsureDefaults(settings ?? new AppCaptureSettings());
            }
            catch
            {
                // If import fails, return defaults and let caller handle the error
                return new AppCaptureSettings();
            }
        }

        /// <summary>
        /// Exports settings to a file at the specified path.
        /// </summary>
        public static void ExportToFile(AppCaptureSettings settings, string filePath)
        {
            var json = ExportToJson(settings);
            File.WriteAllText(filePath, json);
        }

        /// <summary>
        /// Imports settings from a file at the specified path.
        /// </summary>
        public static AppCaptureSettings ImportFromFile(string filePath)
        {
            if (!File.Exists(filePath))
                return new AppCaptureSettings();

            try
            {
                var json = File.ReadAllText(filePath);
                return ImportFromJson(json);
            }
            catch
            {
                return new AppCaptureSettings();
            }
        }

        private static AppCaptureSettings EnsureDefaults(AppCaptureSettings settings)
        {
            // Ensure PostProcessingSettings is not null
            settings.PostProcessing ??= new PostProcessingSettings();
            settings.PostProcessing.Crop ??= new PostProcessingCropSettings();
            settings.PostProcessing.Resize ??= new PostProcessingResizeSettings();

            // Ensure ColorEffectsSettings is not null
            settings.ColorEffects ??= new ColorEffectsSettings();

            // Clamp color effects ranges
            settings.ColorEffects.HueTolerance = Math.Clamp(settings.ColorEffects.HueTolerance, 0, 180);
            settings.ColorEffects.SaturationTolerance = Math.Clamp(settings.ColorEffects.SaturationTolerance, 0, 1);
            settings.ColorEffects.ValueTolerance = Math.Clamp(settings.ColorEffects.ValueTolerance, 0, 1);
            settings.ColorEffects.EffectStrength = Math.Clamp(settings.ColorEffects.EffectStrength, 0, 1);

            // Ensure zoom level is valid
            if (settings.ZoomLevel < 0.1)
                settings.ZoomLevel = 1.0;
            if (settings.ZoomLevel > 5.0)
                settings.ZoomLevel = 1.0;

            return settings;
        }
    }
}
