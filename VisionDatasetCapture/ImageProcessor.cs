using System;
using System.Drawing;
using System.Drawing.Imaging;

namespace VisionDatasetCapture
{
    /// <summary>
    /// Single processing pipeline for image post-processing.
    /// Processing order: Crop → Brightness → Contrast → Saturation → Gamma → Grayscale → Resize
    /// </summary>
    public static class ImageProcessor
    {
        /// <summary>
        /// Process a raw bitmap image according to post-processing settings.
        /// Returns a new processed bitmap; does not modify the input.
        /// </summary>
        public static Bitmap Process(Bitmap rawImage, PostProcessingSettings settings)
        {
            if (rawImage == null)
                throw new ArgumentNullException(nameof(rawImage));
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            // If processing is disabled, return a clone of the raw image
            if (!settings.Enabled)
                return (Bitmap)rawImage.Clone();

            // Start with raw image
            var current = (Bitmap)rawImage.Clone();

            try
            {
                // Step 1: Crop
                if (settings.Crop.Enabled && settings.Crop.Width > 0 && settings.Crop.Height > 0)
                {
                    var cropped = ApplyCrop(current, settings.Crop);
                    if (cropped != current)
                    {
                        current.Dispose();
                        current = cropped;
                    }
                }

                // Step 2: Brightness
                if (Math.Abs(settings.Brightness) > 0.001f)
                {
                    ApplyBrightness(current, settings.Brightness);
                }

                // Step 3: Contrast
                if (Math.Abs(settings.Contrast - 1f) > 0.001f)
                {
                    ApplyContrast(current, settings.Contrast);
                }

                // Step 4: Saturation
                if (Math.Abs(settings.Saturation - 1f) > 0.001f)
                {
                    ApplySaturation(current, settings.Saturation);
                }

                // Step 5: Gamma
                if (Math.Abs(settings.Gamma - 1f) > 0.001f)
                {
                    ApplyGamma(current, settings.Gamma);
                }

                // Step 6: Grayscale
                if (settings.Grayscale)
                {
                    var grayscale = ApplyGrayscale(current);
                    current.Dispose();
                    current = grayscale;
                }

                // Step 7: Resize
                if (settings.Resize.Enabled && settings.Resize.Width > 0 && settings.Resize.Height > 0)
                {
                    var resized = ApplyResize(current, settings.Resize.Width, settings.Resize.Height);
                    if (resized != current)
                    {
                        current.Dispose();
                        current = resized;
                    }
                }

                return current;
            }
            catch
            {
                current.Dispose();
                throw;
            }
        }

        private static Bitmap ApplyCrop(Bitmap image, PostProcessingCropSettings cropSettings)
        {
            var x = Math.Max(0, cropSettings.X);
            var y = Math.Max(0, cropSettings.Y);
            var width = Math.Min(cropSettings.Width, image.Width - x);
            var height = Math.Min(cropSettings.Height, image.Height - y);

            if (width <= 0 || height <= 0)
                return image; // Invalid crop region

            if (x == 0 && y == 0 && width == image.Width && height == image.Height)
                return image; // No-op crop

            var rect = new Rectangle(x, y, width, height);
            return image.Clone(rect, image.PixelFormat);
        }

        private static void ApplyBrightness(Bitmap image, float brightness)
        {
            // brightness: -1 to 1, where 0 = no change
            if (image.PixelFormat != PixelFormat.Format32bppArgb)
                return; // Only support ARGB for safety

            var data = image.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.ReadWrite, image.PixelFormat);
            try
            {
                unsafe
                {
                    var ptr = (int*)data.Scan0;
                    int stride = data.Stride / 4;
                    int pixels = image.Width * image.Height;

                    for (int i = 0; i < pixels; i++)
                    {
                        int pixelIndex = i;
                        int argb = ptr[pixelIndex];

                        byte b = (byte)(argb & 0xFF);
                        byte g = (byte)((argb >> 8) & 0xFF);
                        byte r = (byte)((argb >> 16) & 0xFF);
                        byte a = (byte)((argb >> 24) & 0xFF);

                        // Apply brightness
                        float scale = brightness > 0 ? (1f + brightness) : (1f + brightness);
                        r = ClampByte((int)(r * scale));
                        g = ClampByte((int)(g * scale));
                        b = ClampByte((int)(b * scale));

                        ptr[pixelIndex] = (a << 24) | (r << 16) | (g << 8) | b;
                    }
                }
            }
            finally
            {
                image.UnlockBits(data);
            }
        }

        private static void ApplyContrast(Bitmap image, float contrast)
        {
            // contrast: 0.1 to 3.0, where 1 = no change
            // Formula: output = (input - 128) * contrast + 128
            if (image.PixelFormat != PixelFormat.Format32bppArgb)
                return;

            var data = image.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.ReadWrite, image.PixelFormat);
            try
            {
                unsafe
                {
                    var ptr = (int*)data.Scan0;
                    int pixels = image.Width * image.Height;

                    for (int i = 0; i < pixels; i++)
                    {
                        int argb = ptr[i];

                        byte b = (byte)(argb & 0xFF);
                        byte g = (byte)((argb >> 8) & 0xFF);
                        byte r = (byte)((argb >> 16) & 0xFF);
                        byte a = (byte)((argb >> 24) & 0xFF);

                        r = ClampByte((int)((r - 128) * contrast + 128));
                        g = ClampByte((int)((g - 128) * contrast + 128));
                        b = ClampByte((int)((b - 128) * contrast + 128));

                        ptr[i] = (a << 24) | (r << 16) | (g << 8) | b;
                    }
                }
            }
            finally
            {
                image.UnlockBits(data);
            }
        }

        private static void ApplySaturation(Bitmap image, float saturation)
        {
            // saturation: 0 to 2, where 1 = no change
            // 0 = grayscale, 2 = double saturation
            if (image.PixelFormat != PixelFormat.Format32bppArgb)
                return;

            var data = image.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.ReadWrite, image.PixelFormat);
            try
            {
                unsafe
                {
                    var ptr = (int*)data.Scan0;
                    int pixels = image.Width * image.Height;

                    for (int i = 0; i < pixels; i++)
                    {
                        int argb = ptr[i];

                        byte b = (byte)(argb & 0xFF);
                        byte g = (byte)((argb >> 8) & 0xFF);
                        byte r = (byte)((argb >> 16) & 0xFF);
                        byte a = (byte)((argb >> 24) & 0xFF);

                        RgbToHsb(r, g, b, out float h, out float s, out float br);
                        s = Math.Max(0, Math.Min(1, s * saturation));
                        HsbToRgb(h, s, br, out r, out g, out b);

                        ptr[i] = (a << 24) | (r << 16) | (g << 8) | b;
                    }
                }
            }
            finally
            {
                image.UnlockBits(data);
            }
        }

        private static void ApplyGamma(Bitmap image, float gamma)
        {
            // gamma: 0.1 to 3.0, where 1 = no change
            if (image.PixelFormat != PixelFormat.Format32bppArgb)
                return;

            var gammaLut = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                gammaLut[i] = ClampByte((int)(Math.Pow(i / 255.0, 1.0 / gamma) * 255.0));
            }

            var data = image.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.ReadWrite, image.PixelFormat);
            try
            {
                unsafe
                {
                    var ptr = (int*)data.Scan0;
                    int pixels = image.Width * image.Height;

                    for (int i = 0; i < pixels; i++)
                    {
                        int argb = ptr[i];

                        byte b = (byte)(argb & 0xFF);
                        byte g = (byte)((argb >> 8) & 0xFF);
                        byte r = (byte)((argb >> 16) & 0xFF);
                        byte a = (byte)((argb >> 24) & 0xFF);

                        r = gammaLut[r];
                        g = gammaLut[g];
                        b = gammaLut[b];

                        ptr[i] = (a << 24) | (r << 16) | (g << 8) | b;
                    }
                }
            }
            finally
            {
                image.UnlockBits(data);
            }
        }

        private static Bitmap ApplyGrayscale(Bitmap image)
        {
            var grayscaleImage = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppArgb);

            var sourceData = image.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.ReadOnly, image.PixelFormat);
            var destData = grayscaleImage.LockBits(new Rectangle(0, 0, grayscaleImage.Width, grayscaleImage.Height), ImageLockMode.WriteOnly, grayscaleImage.PixelFormat);

            try
            {
                unsafe
                {
                    var srcPtr = (int*)sourceData.Scan0;
                    var dstPtr = (int*)destData.Scan0;
                    int pixels = image.Width * image.Height;

                    for (int i = 0; i < pixels; i++)
                    {
                        int argb = srcPtr[i];

                        byte b = (byte)(argb & 0xFF);
                        byte g = (byte)((argb >> 8) & 0xFF);
                        byte r = (byte)((argb >> 16) & 0xFF);
                        byte a = (byte)((argb >> 24) & 0xFF);

                        // ITU-R BT.601 grayscale conversion
                        byte gray = (byte)(0.299 * r + 0.587 * g + 0.114 * b);

                        dstPtr[i] = (a << 24) | (gray << 16) | (gray << 8) | gray;
                    }
                }
            }
            finally
            {
                image.UnlockBits(sourceData);
                grayscaleImage.UnlockBits(destData);
            }

            return grayscaleImage;
        }

        private static Bitmap ApplyResize(Bitmap image, int newWidth, int newHeight)
        {
            if (newWidth <= 0 || newHeight <= 0)
                return image;

            if (image.Width == newWidth && image.Height == newHeight)
                return image;

            var resized = new Bitmap(newWidth, newHeight, PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(resized);
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(image, 0, 0, newWidth, newHeight);
            return resized;
        }

        private static byte ClampByte(int value)
        {
            if (value < 0) return 0;
            if (value > 255) return 255;
            return (byte)value;
        }

        private static void RgbToHsb(byte r, byte g, byte b, out float h, out float s, out float br)
        {
            float rf = r / 255f;
            float gf = g / 255f;
            float bf = b / 255f;

            float max = Math.Max(Math.Max(rf, gf), bf);
            float min = Math.Min(Math.Min(rf, gf), bf);
            float delta = max - min;

            br = max;

            if (max == 0)
                s = 0;
            else
                s = delta / max;

            if (delta == 0)
                h = 0;
            else if (max == rf)
                h = ((gf - bf) / delta) % 6;
            else if (max == gf)
                h = (bf - rf) / delta + 2;
            else
                h = (rf - gf) / delta + 4;

            h = (h * 60f) / 360f; // Normalize to 0-1
            if (h < 0) h += 1;
        }

        private static void HsbToRgb(float h, float s, float br, out byte r, out byte g, out byte b)
        {
            h = h * 360f; // Denormalize from 0-1 to 0-360
            float c = br * s;
            float x = c * (1 - Math.Abs((h / 60f) % 2 - 1));
            float m = br - c;

            float rf, gf, bf;

            if (h < 60)
            {
                rf = c;
                gf = x;
                bf = 0;
            }
            else if (h < 120)
            {
                rf = x;
                gf = c;
                bf = 0;
            }
            else if (h < 180)
            {
                rf = 0;
                gf = c;
                bf = x;
            }
            else if (h < 240)
            {
                rf = 0;
                gf = x;
                bf = c;
            }
            else if (h < 300)
            {
                rf = x;
                gf = 0;
                bf = c;
            }
            else
            {
                rf = c;
                gf = 0;
                bf = x;
            }

            r = ClampByte((int)((rf + m) * 255));
            g = ClampByte((int)((gf + m) * 255));
            b = ClampByte((int)((bf + m) * 255));
        }
    }
}
