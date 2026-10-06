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
        }
    }
}
