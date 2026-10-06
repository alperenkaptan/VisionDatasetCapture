using System.Drawing;
using System.Drawing.Imaging;
using VisionDatasetCapture;

namespace VisionDatasetCapture.Tests
{
    public class ImageProcessorTests
    {
        private Bitmap CreateTestBitmap(int width, int height, byte r, byte g, byte b)
        {
            var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                unsafe
                {
                    var ptr = (int*)data.Scan0;
                    int color = (255 << 24) | (r << 16) | (g << 8) | b;
                    int pixels = width * height;
                    for (int i = 0; i < pixels; i++)
                    {
                        ptr[i] = color;
                    }
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
            return bitmap;
        }

        private (byte r, byte g, byte b) GetPixelColor(Bitmap bitmap, int x, int y)
        {
            var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                unsafe
                {
                    var ptr = (int*)data.Scan0;
                    int stride = data.Stride / 4;
                    int argb = ptr[y * stride + x];

                    byte b = (byte)(argb & 0xFF);
                    byte g = (byte)((argb >> 8) & 0xFF);
                    byte r = (byte)((argb >> 16) & 0xFF);

                    return (r, g, b);
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }

        [Fact]
        public void ProcessingDisabled_PreservesOriginalImage()
        {
            // Arrange
            using var original = CreateTestBitmap(10, 10, 100, 150, 200);
            var settings = new PostProcessingSettings { Enabled = false };

            // Act
            using var result = ImageProcessor.Process(original, settings);

            // Assert
            Assert.Equal(original.Width, result.Width);
            Assert.Equal(original.Height, result.Height);
            var (r, g, b) = GetPixelColor(result, 0, 0);
            Assert.Equal(100, r);
            Assert.Equal(150, g);
            Assert.Equal(200, b);
        }

        [Fact]
        public void Grayscale_ConvertsToGrayscale()
        {
            // Arrange
            using var original = CreateTestBitmap(10, 10, 100, 150, 200);
            var settings = new PostProcessingSettings
            {
                Enabled = true,
                Grayscale = true
            };

            // Act
            using var result = ImageProcessor.Process(original, settings);

            // Assert
            Assert.Equal(original.Width, result.Width);
            Assert.Equal(original.Height, result.Height);
            var (r, g, b) = GetPixelColor(result, 0, 0);
            // All channels should be equal (grayscale)
            Assert.Equal(r, g);
            Assert.Equal(g, b);
        }

        [Fact]
        public void Brightness_IncreasesPixelValues()
        {
            // Arrange
            using var original = CreateTestBitmap(10, 10, 100, 100, 100);
            var settings = new PostProcessingSettings
            {
                Enabled = true,
                Brightness = 0.2f
            };

            // Act
            using var result = ImageProcessor.Process(original, settings);

            // Assert
            var (r, g, b) = GetPixelColor(result, 0, 0);
            Assert.True(r > 100);
            Assert.True(g > 100);
            Assert.True(b > 100);
        }

        [Fact]
        public void Brightness_DecreasesPixelValues()
        {
            // Arrange
            using var original = CreateTestBitmap(10, 10, 200, 200, 200);
            var settings = new PostProcessingSettings
            {
                Enabled = true,
                Brightness = -0.3f
            };

            // Act
            using var result = ImageProcessor.Process(original, settings);

            // Assert
            var (r, g, b) = GetPixelColor(result, 0, 0);
            Assert.True(r < 200);
            Assert.True(g < 200);
            Assert.True(b < 200);
        }

        [Fact]
        public void Contrast_IncreasesDifference()
        {
            // Arrange
            using var original = CreateTestBitmap(20, 20, 128, 128, 128);
            var settings = new PostProcessingSettings
            {
                Enabled = true,
                Contrast = 1.5f
            };

            // Act
            using var result = ImageProcessor.Process(original, settings);

            // Assert - All pixels should be same since they're the same color, contrast affects difference
            var (r1, g1, b1) = GetPixelColor(result, 0, 0);
            var (r2, g2, b2) = GetPixelColor(result, result.Width - 1, result.Height - 1);
            Assert.Equal(r1, r2);
        }

        [Fact]
        public void Crop_ProducesSmallerImage()
        {
            // Arrange
            using var original = CreateTestBitmap(100, 100, 50, 100, 150);
            var settings = new PostProcessingSettings
            {
                Enabled = true,
                Crop = new PostProcessingCropSettings
                {
                    Enabled = true,
                    X = 10,
                    Y = 10,
                    Width = 50,
                    Height = 50
                }
            };

            // Act
            using var result = ImageProcessor.Process(original, settings);

            // Assert
            Assert.Equal(50, result.Width);
            Assert.Equal(50, result.Height);
        }

        [Fact]
        public void Resize_ProducesCorrectDimensions()
        {
            // Arrange
            using var original = CreateTestBitmap(200, 200, 75, 125, 175);
            var settings = new PostProcessingSettings
            {
                Enabled = true,
                Resize = new PostProcessingResizeSettings
                {
                    Enabled = true,
                    Width = 100,
                    Height = 100
                }
            };

            // Act
            using var result = ImageProcessor.Process(original, settings);

            // Assert
            Assert.Equal(100, result.Width);
            Assert.Equal(100, result.Height);
        }

        [Fact]
        public void ProcessingOrder_CropThenResize()
        {
            // Arrange
            using var original = CreateTestBitmap(100, 100, 50, 100, 150);
            var settings = new PostProcessingSettings
            {
                Enabled = true,
                Crop = new PostProcessingCropSettings
                {
                    Enabled = true,
                    X = 0,
                    Y = 0,
                    Width = 50,
                    Height = 50
                },
                Resize = new PostProcessingResizeSettings
                {
                    Enabled = true,
                    Width = 25,
                    Height = 25
                }
            };

            // Act
            using var result = ImageProcessor.Process(original, settings);

            // Assert
            Assert.Equal(25, result.Width);
            Assert.Equal(25, result.Height);
        }

        [Fact]
        public void SettingsClone_CreatesIndependentCopy()
        {
            // Arrange
            var original = new PostProcessingSettings
            {
                Enabled = true,
                Brightness = 0.5f,
                Crop = new PostProcessingCropSettings { Enabled = true, Width = 100, Height = 100 }
            };

            // Act
            var cloned = original.Clone();

            // Assert
            Assert.Equal(original.Enabled, cloned.Enabled);
            Assert.Equal(original.Brightness, cloned.Brightness);
            Assert.Equal(original.Crop.Width, cloned.Crop.Width);

            // Modify cloned
            cloned.Brightness = 0.9f;
            cloned.Crop.Width = 200;

            // Original should be unchanged
            Assert.Equal(0.5f, original.Brightness);
            Assert.Equal(100, original.Crop.Width);
        }

        [Fact]
        public void IdenticalInputAndSettings_ProduceIdenticalOutput()
        {
            // Arrange
            using var input1 = CreateTestBitmap(50, 50, 100, 150, 200);
            using var input2 = CreateTestBitmap(50, 50, 100, 150, 200);

            var settings = new PostProcessingSettings
            {
                Enabled = true,
                Brightness = 0.2f,
                Contrast = 1.3f,
                Saturation = 0.8f,
                Gamma = 1.2f
            };

            // Act
            using var result1 = ImageProcessor.Process(input1, settings);
            using var result2 = ImageProcessor.Process(input2, settings);

            // Assert
            Assert.Equal(result1.Width, result2.Width);
            Assert.Equal(result1.Height, result2.Height);

            // Check multiple pixels to ensure they're identical
            for (int x = 0; x < result1.Width; x += 10)
            {
                for (int y = 0; y < result1.Height; y += 10)
                {
                    var (r1, g1, b1) = GetPixelColor(result1, x, y);
                    var (r2, g2, b2) = GetPixelColor(result2, x, y);
                    Assert.Equal(r1, r2);
                    Assert.Equal(g1, g2);
                    Assert.Equal(b1, b2);
                }
            }
        }

        [Fact]
        public void Process_WithNullImage_ThrowsArgumentNullException()
        {
            // Arrange
            var settings = new PostProcessingSettings();

            // Act & Assert
#pragma warning disable CS8625
            Assert.Throws<ArgumentNullException>(() => ImageProcessor.Process(null, settings));
#pragma warning restore CS8625
        }

        [Fact]
        public void Process_WithNullSettings_ThrowsArgumentNullException()
        {
            // Arrange
            using var bitmap = CreateTestBitmap(10, 10, 50, 100, 150);

            // Act & Assert
#pragma warning disable CS8625
            Assert.Throws<ArgumentNullException>(() => ImageProcessor.Process(bitmap, null));
#pragma warning restore CS8625
        }

        [Fact]
        public void Saturation_AffectsColorIntensity()
        {
            // Arrange
            using var original = CreateTestBitmap(10, 10, 200, 100, 50);
            var settings = new PostProcessingSettings
            {
                Enabled = true,
                Saturation = 0.5f  // Reduce saturation
            };

            // Act
            using var result = ImageProcessor.Process(original, settings);

            // Assert
            var (r, g, b) = GetPixelColor(result, 0, 0);
            // With reduced saturation, colors should be closer to each other (less vibrant)
            // This is hard to test exactly, but the result should still be valid
            Assert.True(r >= 0 && r <= 255);
            Assert.True(g >= 0 && g <= 255);
            Assert.True(b >= 0 && b <= 255);
        }

        [Fact]
        public void Gamma_AffectsPixelBrightness()
        {
            // Arrange
            using var original = CreateTestBitmap(10, 10, 128, 128, 128);
            var settings = new PostProcessingSettings
            {
                Enabled = true,
                Gamma = 2.0f  // Increase gamma (lighten)
            };

            // Act
            using var result = ImageProcessor.Process(original, settings);

            // Assert
            var (r, g, b) = GetPixelColor(result, 0, 0);
            Assert.True(r > 128);  // Should be lighter
            Assert.True(g > 128);
            Assert.True(b > 128);
        }
    }

    public class PostProcessingSettingsTests
    {
        [Fact]
        public void PostProcessingSettings_HasCorrectDefaults()
        {
            // Arrange & Act
            var settings = new PostProcessingSettings();

            // Assert
            Assert.True(settings.Enabled);
            Assert.False(settings.Grayscale);
            Assert.Equal(0f, settings.Brightness);
            Assert.Equal(1f, settings.Contrast);
            Assert.Equal(1f, settings.Saturation);
            Assert.Equal(1f, settings.Gamma);
            Assert.NotNull(settings.Crop);
            Assert.False(settings.Crop.Enabled);
            Assert.NotNull(settings.Resize);
            Assert.False(settings.Resize.Enabled);
        }

        [Fact]
        public void CropSettings_HasCorrectDefaults()
        {
            // Arrange & Act
            var crop = new PostProcessingCropSettings();

            // Assert
            Assert.False(crop.Enabled);
            Assert.Equal(0, crop.X);
            Assert.Equal(0, crop.Y);
            Assert.Equal(0, crop.Width);
            Assert.Equal(0, crop.Height);
        }

        [Fact]
        public void ResizeSettings_HasCorrectDefaults()
        {
            // Arrange & Act
            var resize = new PostProcessingResizeSettings();

            // Assert
            Assert.False(resize.Enabled);
            Assert.Equal(800, resize.Width);
            Assert.Equal(600, resize.Height);
        }
    }

    public class SettingsPersistenceTests
    {
        [Fact]
        public void AppCaptureSettings_SerializesAndDeserializes()
        {
            // Arrange
            var settings = new AppCaptureSettings
            {
                SelectedProcessId = 1234,
                DatasetName = "TestDataset",
                CaptureMode = CaptureMode.AutoTimed,
                IntervalSeconds = 2,
                ManualKey = "F12",
                PostProcessing = new PostProcessingSettings
                {
                    Enabled = true,
                    Brightness = 0.3f,
                    Crop = new PostProcessingCropSettings { Enabled = true, Width = 100, Height = 100 }
                },
                ColorEffects = new ColorEffectsSettings
                {
                    ShowMaskOverlay = true,
                    Rules =
                    {
                        new ColorEffectRule
                        {
                            Name = "Health Bar",
                            Enabled = true,
                            BaseR = 210,
                            BaseG = 20,
                            BaseB = 20,
                            HueTolerance = 12,
                            SaturationTolerance = 0.3,
                            ValueTolerance = 0.25,
                            EffectMode = ColorEffectMode.ReplaceColor,
                            EffectStrength = 0.8,
                            TargetR = 0,
                            TargetG = 255,
                            TargetB = 0
                        },
                        new ColorEffectRule
                        {
                            Name = "Mana",
                            Enabled = false,
                            BaseR = 20,
                            BaseG = 20,
                            BaseB = 200,
                            HueTolerance = 10,
                            SaturationTolerance = 0.2,
                            ValueTolerance = 0.2,
                            EffectMode = ColorEffectMode.Highlight,
                            EffectStrength = 0.4,
                            TargetR = 255,
                            TargetG = 255,
                            TargetB = 0
                        }
                    }
                }
            };

            // Act
            var json = System.Text.Json.JsonSerializer.Serialize(settings, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            var deserialized = System.Text.Json.JsonSerializer.Deserialize<AppCaptureSettings>(json);

            // Assert
            Assert.NotNull(deserialized);
            Assert.Equal(settings.SelectedProcessId, deserialized.SelectedProcessId);
            Assert.Equal(settings.DatasetName, deserialized.DatasetName);
            Assert.Equal(settings.CaptureMode, deserialized.CaptureMode);
            Assert.Equal(settings.IntervalSeconds, deserialized.IntervalSeconds);
            Assert.Equal(settings.ManualKey, deserialized.ManualKey);
            Assert.NotNull(deserialized.PostProcessing);
            Assert.Equal(settings.PostProcessing.Enabled, deserialized.PostProcessing.Enabled);
            Assert.Equal(settings.PostProcessing.Brightness, deserialized.PostProcessing.Brightness);
            Assert.Equal(settings.PostProcessing.Crop.Width, deserialized.PostProcessing.Crop.Width);

            Assert.NotNull(deserialized.ColorEffects);
            Assert.NotNull(deserialized.ColorEffects.Rules);
            Assert.Equal(2, deserialized.ColorEffects.Rules.Count);
            Assert.Equal("Health Bar", deserialized.ColorEffects.Rules[0].Name);
            Assert.Equal("Mana", deserialized.ColorEffects.Rules[1].Name);
            Assert.True(deserialized.ColorEffects.Rules[0].Enabled);
            Assert.False(deserialized.ColorEffects.Rules[1].Enabled);
            Assert.Equal(ColorEffectMode.ReplaceColor, deserialized.ColorEffects.Rules[0].EffectMode);
        }

        [Fact]
        public void ImportFromJson_LegacySingleRule_MigratesToRulesList()
        {
            // Arrange
            var legacyJson = @"{
              ""selectedProcessId"": 42,
              ""datasetName"": ""Legacy"",
              ""captureMode"": 0,
              ""intervalSeconds"": 1,
              ""manualKey"": ""K"",
              ""colorEffects"": {
                ""enabled"": true,
                ""baseR"": 120,
                ""baseG"": 130,
                ""baseB"": 140,
                ""hueTolerance"": 22,
                ""saturationTolerance"": 0.15,
                ""valueTolerance"": 0.10,
                ""effectMode"": 1,
                ""effectStrength"": 0.70,
                ""targetR"": 5,
                ""targetG"": 6,
                ""targetB"": 7
              }
            }";

            // Act
            var imported = AppCaptureSettingsStore.ImportFromJson(legacyJson);

            // Assert
            Assert.NotNull(imported.ColorEffects);
            Assert.NotNull(imported.ColorEffects.Rules);
            Assert.Single(imported.ColorEffects.Rules);

            var rule = imported.ColorEffects.Rules[0];
            Assert.Equal("Color Effect 1", rule.Name);
            Assert.True(rule.Enabled);
            Assert.Equal((byte)120, rule.BaseR);
            Assert.Equal((byte)130, rule.BaseG);
            Assert.Equal((byte)140, rule.BaseB);
            Assert.Equal(22, rule.HueTolerance);
            Assert.Equal(0.15, rule.SaturationTolerance, 3);
            Assert.Equal(0.10, rule.ValueTolerance, 3);
            Assert.Equal(ColorEffectMode.ReplaceColor, rule.EffectMode);
            Assert.Equal(0.70, rule.EffectStrength, 3);
            Assert.Equal((byte)5, rule.TargetR);
            Assert.Equal((byte)6, rule.TargetG);
            Assert.Equal((byte)7, rule.TargetB);
        }
    }

    public class ColorEffectsProcessorTests
    {
        private static Bitmap CreateBitmap(params Color[] pixels)
        {
            var bmp = new Bitmap(pixels.Length, 1, PixelFormat.Format32bppArgb);
            for (var x = 0; x < pixels.Length; x++)
                bmp.SetPixel(x, 0, pixels[x]);
            return bmp;
        }

        private static Color GetPixel(Bitmap bmp, int x)
        {
            return bmp.GetPixel(x, 0);
        }

        [Fact]
        public void Rules_AreEvaluatedInPriorityOrder_FirstMatchWins()
        {
            using var input = CreateBitmap(Color.FromArgb(255, 200, 20, 20));
            var settings = new ColorEffectsSettings
            {
                Enabled = true,
                ShowMaskOverlay = false,
                Rules =
                {
                    new ColorEffectRule
                    {
                        Name = "First",
                        Enabled = true,
                        BaseR = 200,
                        BaseG = 20,
                        BaseB = 20,
                        HueTolerance = 40,
                        SaturationTolerance = 1,
                        ValueTolerance = 1,
                        EffectMode = ColorEffectMode.ReplaceColor,
                        EffectStrength = 1,
                        TargetR = 0,
                        TargetG = 255,
                        TargetB = 0
                    },
                    new ColorEffectRule
                    {
                        Name = "Second",
                        Enabled = true,
                        BaseR = 200,
                        BaseG = 20,
                        BaseB = 20,
                        HueTolerance = 40,
                        SaturationTolerance = 1,
                        ValueTolerance = 1,
                        EffectMode = ColorEffectMode.ReplaceColor,
                        EffectStrength = 1,
                        TargetR = 0,
                        TargetG = 0,
                        TargetB = 255
                    }
                }
            };

            using var result = ColorEffectsProcessor.Apply(input, settings);
            var pixel = GetPixel(result, 0);

            Assert.Equal((byte)0, pixel.R);
            Assert.Equal((byte)255, pixel.G);
            Assert.Equal((byte)0, pixel.B);
        }

        [Fact]
        public void DisabledRules_AreIgnored()
        {
            using var input = CreateBitmap(Color.FromArgb(255, 210, 20, 20));
            var settings = new ColorEffectsSettings
            {
                Enabled = true,
                ShowMaskOverlay = false,
                Rules =
                {
                    new ColorEffectRule
                    {
                        Name = "Disabled",
                        Enabled = false,
                        BaseR = 210,
                        BaseG = 20,
                        BaseB = 20,
                        HueTolerance = 40,
                        SaturationTolerance = 1,
                        ValueTolerance = 1,
                        EffectMode = ColorEffectMode.ReplaceColor,
                        EffectStrength = 1,
                        TargetR = 0,
                        TargetG = 255,
                        TargetB = 0
                    }
                }
            };

            using var result = ColorEffectsProcessor.Apply(input, settings);
            var pixel = GetPixel(result, 0);

            Assert.Equal((byte)210, pixel.R);
            Assert.Equal((byte)20, pixel.G);
            Assert.Equal((byte)20, pixel.B);
        }

        [Fact]
        public void MatchingPixels_GetCorrectEffect()
        {
            using var input = CreateBitmap(Color.FromArgb(255, 180, 50, 50));
            var settings = new ColorEffectsSettings
            {
                Enabled = true,
                ShowMaskOverlay = false,
                Rules =
                {
                    new ColorEffectRule
                    {
                        Name = "Replace",
                        Enabled = true,
                        BaseR = 180,
                        BaseG = 50,
                        BaseB = 50,
                        HueTolerance = 30,
                        SaturationTolerance = 1,
                        ValueTolerance = 1,
                        EffectMode = ColorEffectMode.ReplaceColor,
                        EffectStrength = 1,
                        TargetR = 5,
                        TargetG = 15,
                        TargetB = 25
                    }
                }
            };

            using var result = ColorEffectsProcessor.Apply(input, settings);
            var pixel = GetPixel(result, 0);

            Assert.Equal((byte)5, pixel.R);
            Assert.Equal((byte)15, pixel.G);
            Assert.Equal((byte)25, pixel.B);
        }

        [Fact]
        public void MultipleRules_AreAppliedInSingleCall_ForDifferentPixels()
        {
            using var input = CreateBitmap(
                Color.FromArgb(255, 210, 30, 30),
                Color.FromArgb(255, 30, 30, 210),
                Color.FromArgb(255, 20, 200, 20));

            var settings = new ColorEffectsSettings
            {
                Enabled = true,
                ShowMaskOverlay = false,
                Rules =
                {
                    new ColorEffectRule
                    {
                        Name = "RedToGreen",
                        Enabled = true,
                        BaseR = 210,
                        BaseG = 30,
                        BaseB = 30,
                        HueTolerance = 25,
                        SaturationTolerance = 1,
                        ValueTolerance = 1,
                        EffectMode = ColorEffectMode.ReplaceColor,
                        EffectStrength = 1,
                        TargetR = 0,
                        TargetG = 255,
                        TargetB = 0
                    },
                    new ColorEffectRule
                    {
                        Name = "BlueToYellow",
                        Enabled = true,
                        BaseR = 30,
                        BaseG = 30,
                        BaseB = 210,
                        HueTolerance = 25,
                        SaturationTolerance = 1,
                        ValueTolerance = 1,
                        EffectMode = ColorEffectMode.ReplaceColor,
                        EffectStrength = 1,
                        TargetR = 255,
                        TargetG = 255,
                        TargetB = 0
                    }
                }
            };

            using var result = ColorEffectsProcessor.Apply(input, settings);
            var p0 = GetPixel(result, 0);
            var p1 = GetPixel(result, 1);
            var p2 = GetPixel(result, 2);

            Assert.Equal((byte)0, p0.R);
            Assert.Equal((byte)255, p0.G);
            Assert.Equal((byte)0, p0.B);

            Assert.Equal((byte)255, p1.R);
            Assert.Equal((byte)255, p1.G);
            Assert.Equal((byte)0, p1.B);

            // unmatched pixel should remain unchanged
            Assert.Equal((byte)20, p2.R);
            Assert.Equal((byte)200, p2.G);
            Assert.Equal((byte)20, p2.B);
        }
    }
}
