using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace VisionDatasetCapture
{
    public static class DatasetWriter
    {
        public static bool IsValidDatasetName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;

            if (name.EndsWith('.') || name.EndsWith(' '))
                return false;

            var invalidChars = Path.GetInvalidFileNameChars();
            return !name.Any(ch => invalidChars.Contains(ch));
        }

        public static string GetDatasetFolderPath(string datasetName)
        {
            var applicationDirectory = AppDomain.CurrentDomain.BaseDirectory;
            return Path.Combine(applicationDirectory, datasetName);
        }

        public static void EnsureDatasetFolderExists(string datasetName)
        {
            var folderPath = GetDatasetFolderPath(datasetName);
            Directory.CreateDirectory(folderPath);
        }

        public static int GetNextImageNumber(string datasetName)
        {
            var folderPath = GetDatasetFolderPath(datasetName);

            if (!Directory.Exists(folderPath))
                return 0;

            var pattern = $"^{Regex.Escape(datasetName)}_([0-9]+)\\.png$";
            var regex = new Regex(pattern, RegexOptions.IgnoreCase);

            var numbers = Directory
                .GetFiles(folderPath, $"{datasetName}_*.png")
                .Select(f => Path.GetFileName(f))
                .Where(f => regex.IsMatch(f))
                .Select(f =>
                {
                    var match = regex.Match(f);
                    if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var num))
                        return num;
                    return -1;
                })
                .Where(n => n >= 0)
                .ToList();

            if (numbers.Count == 0)
                return 0;

            return numbers.Max() + 1;
        }

        public static string SaveScreenshot(string datasetName, int imageNumber, System.Drawing.Bitmap bitmap)
        {
            EnsureDatasetFolderExists(datasetName);
            var folderPath = GetDatasetFolderPath(datasetName);
            var filename = $"{datasetName}_{imageNumber}.png";
            var filePath = Path.Combine(folderPath, filename);

            bitmap.Save(filePath, System.Drawing.Imaging.ImageFormat.Png);
            return filename;
        }
    }
}
