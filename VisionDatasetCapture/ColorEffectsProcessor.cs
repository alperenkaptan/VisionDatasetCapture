using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;

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
            if (!settings.Enabled || settings.Rules == null || settings.Rules.Count == 0)
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

            var ruleSnapshots = settings.Rules
                .Where(r => r != null && r.Enabled)
                .Select(r =>
                {
                    RgbToHsv(r.BaseR, r.BaseG, r.BaseB, out var baseH, out var baseS, out var baseV);
                    return new RuleSnapshot(
                        r,
                        baseH,
                        baseS,
                        baseV,
                        Math.Clamp(r.HueTolerance, 0, 180),
                        Math.Clamp(r.SaturationTolerance, 0, 1),
                        Math.Clamp(r.ValueTolerance, 0, 1),
                        Math.Clamp(r.EffectStrength, 0, 1));
                })
                .ToArray();

            if (ruleSnapshots.Length == 0)
                return output;

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

                            for (var i = 0; i < ruleSnapshots.Length; i++)
                            {
                                var rule = ruleSnapshots[i];
                                var hueDelta = Math.Abs(rule.BaseH - h);
                                if (hueDelta > 180)
                                    hueDelta = 360 - hueDelta;

                                if (hueDelta > rule.HueTolerance || Math.Abs(rule.BaseS - s) > rule.SaturationTolerance || Math.Abs(rule.BaseV - v) > rule.ValueTolerance)
                                    continue;

                                if (showMaskOverlay)
                                {
                                    // Red mask overlay for matched pixels
                                    p[0] = (byte)(b * 0.35);
                                    p[1] = (byte)(g * 0.35);
                                    p[2] = ClampByte((int)(r * 0.35 + 255 * 0.65));
                                    p[3] = a;
                                }
                                else
                                {
                                    ApplyEffect(rule.Rule, rule.EffectStrength, ref r, ref g, ref b);
                                    p[0] = b;
                                    p[1] = g;
                                    p[2] = r;
                                    p[3] = a;
                                }

                                break;
                            }
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

        private readonly struct RuleSnapshot
        {
            public RuleSnapshot(
                ColorEffectRule rule,
                double baseH,
                double baseS,
                double baseV,
                double hueTolerance,
                double saturationTolerance,
                double valueTolerance,
                double effectStrength)
            {
                Rule = rule;
                BaseH = baseH;
                BaseS = baseS;
                BaseV = baseV;
                HueTolerance = hueTolerance;
                SaturationTolerance = saturationTolerance;
                ValueTolerance = valueTolerance;
                EffectStrength = effectStrength;
            }

            public ColorEffectRule Rule { get; }
            public double BaseH { get; }
            public double BaseS { get; }
            public double BaseV { get; }
            public double HueTolerance { get; }
            public double SaturationTolerance { get; }
            public double ValueTolerance { get; }
            public double EffectStrength { get; }
        }

        private static void ApplyEffect(ColorEffectRule rule, double strength, ref byte r, ref byte g, ref byte b)
        {
            switch (rule.EffectMode)
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
                    r = Blend(r, rule.TargetR, strength);
                    g = Blend(g, rule.TargetG, strength);
                    b = Blend(b, rule.TargetB, strength);
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
                    r = Blend(r, rule.TargetR, strength);
                    g = Blend(g, rule.TargetG, strength);
                    b = Blend(b, rule.TargetB, strength);
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
