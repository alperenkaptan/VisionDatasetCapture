using System;
using System.Drawing;
using System.Drawing.Imaging;

namespace VisionDatasetCapture
{
    public static class ColorEffectsProcessor
    {
        public static Bitmap Apply(Bitmap input, ColorEffectsSettings settings)
        {
            if (input == null)
                throw new ArgumentNullException(nameof(input));
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            var output = (Bitmap)input.Clone();
            if (!settings.Enabled)
                return output;

            var pixelFormat = output.PixelFormat;
            if (pixelFormat != PixelFormat.Format32bppArgb)
            {
                var converted = new Bitmap(output.Width, output.Height, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(converted))
                {
                    g.DrawImage(output, 0, 0, output.Width, output.Height);
                }
                output.Dispose();
                output = converted;
            }

            RgbToHsv(settings.BaseR, settings.BaseG, settings.BaseB, out var baseH, out var baseS, out var baseV);
            var hueTol = settings.HueTolerance;
            var satTol = settings.SaturationTolerance;
            var valTol = settings.ValueTolerance;
            var strength = settings.EffectStrength;
            var showMaskOverlay = settings.ShowMaskOverlay;

            var data = output.LockBits(
                new Rectangle(0, 0, output.Width, output.Height),
                ImageLockMode.ReadWrite,
                output.PixelFormat);

            try
            {
                unsafe
                {
                    var ptr = (byte*)data.Scan0;
                    for (var y = 0; y < output.Height; y++)
                    {
                        var row = ptr + y * data.Stride;
                        for (var x = 0; x < output.Width; x++)
                        {
                            var p = row + x * 4;
                            byte b = p[0];
                            byte g = p[1];
                            byte r = p[2];
                            byte a = p[3];

                            RgbToHsv(r, g, b, out var h, out var s, out var v);
                            var hueDelta = Math.Abs(baseH - h);
                            if (hueDelta > 180)
                                hueDelta = 360 - hueDelta;

                            if (hueDelta > hueTol || Math.Abs(baseS - s) > satTol || Math.Abs(baseV - v) > valTol)
                                continue;

                            if (showMaskOverlay)
                            {
                                // Red mask overlay for matched pixels
                                p[0] = (byte)(b * 0.35);
                                p[1] = (byte)(g * 0.35);
                                p[2] = ClampByte((int)(r * 0.35 + 255 * 0.65));
                                p[3] = a;
                                continue;
                            }

                            ApplyEffect(settings, strength, ref r, ref g, ref b);

                            p[0] = b;
                            p[1] = g;
                            p[2] = r;
                            p[3] = a;
                        }
                    }
                }
            }
            finally
            {
                output.UnlockBits(data);
            }

            return output;
        }

        private static void ApplyEffect(ColorEffectsSettings settings, double strength, ref byte r, ref byte g, ref byte b)
        {
            switch (settings.EffectMode)
            {
                case ColorEffectMode.Highlight:
                {
                    var boost = 1.0 + (0.8 * strength);
                    r = ClampByte((int)(r * boost));
                    g = ClampByte((int)(g * boost));
                    b = ClampByte((int)(b * boost));
                    break;
                }
                case ColorEffectMode.ReplaceColor:
                {
                    r = Blend(r, settings.TargetR, strength);
                    g = Blend(g, settings.TargetG, strength);
                    b = Blend(b, settings.TargetB, strength);
                    break;
                }
                case ColorEffectMode.Grayscale:
                {
                    var gray = ClampByte((int)(r * 0.299 + g * 0.587 + b * 0.114));
                    r = Blend(r, gray, strength);
                    g = Blend(g, gray, strength);
                    b = Blend(b, gray, strength);
                    break;
                }
                case ColorEffectMode.Brighten:
                {
                    var add = (int)(255 * 0.35 * strength);
                    r = ClampByte(r + add);
                    g = ClampByte(g + add);
                    b = ClampByte(b + add);
                    break;
                }
                case ColorEffectMode.Darken:
                {
                    var mul = 1.0 - (0.7 * strength);
                    r = ClampByte((int)(r * mul));
                    g = ClampByte((int)(g * mul));
                    b = ClampByte((int)(b * mul));
                    break;
                }
                case ColorEffectMode.CustomColor:
                {
                    r = Blend(r, settings.TargetR, strength);
                    g = Blend(g, settings.TargetG, strength);
                    b = Blend(b, settings.TargetB, strength);
                    break;
                }
            }
        }

        private static bool IsMatchHsv(
            double baseH, double baseS, double baseV,
            double h, double s, double v,
            double hueToleranceDegrees,
            double satTolerance,
            double valTolerance)
        {
            var hueDelta = Math.Abs(baseH - h);
            if (hueDelta > 180)
                hueDelta = 360 - hueDelta;

            return hueDelta <= hueToleranceDegrees
                && Math.Abs(baseS - s) <= satTolerance
                && Math.Abs(baseV - v) <= valTolerance;
        }

        private static void RgbToHsv(byte r, byte g, byte b, out double h, out double s, out double v)
        {
            double rf = r / 255.0;
            double gf = g / 255.0;
            double bf = b / 255.0;

            var max = Math.Max(rf, Math.Max(gf, bf));
            var min = Math.Min(rf, Math.Min(gf, bf));
            var delta = max - min;

            h = 0;
            if (delta > 0)
            {
                if (Math.Abs(max - rf) < 0.00001)
                    h = 60 * (((gf - bf) / delta) % 6);
                else if (Math.Abs(max - gf) < 0.00001)
                    h = 60 * (((bf - rf) / delta) + 2);
                else
                    h = 60 * (((rf - gf) / delta) + 4);

                if (h < 0)
                    h += 360;
            }

            s = max <= 0 ? 0 : delta / max;
            v = max;
        }

        private static byte Blend(byte original, byte target, double strength)
        {
            return ClampByte((int)(original + ((target - original) * strength)));
        }

        private static byte ClampByte(int value)
        {
            if (value < 0) return 0;
            if (value > 255) return 255;
            return (byte)value;
        }
    }
}
