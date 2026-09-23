using System.IO;
using System.Windows.Media.Imaging;

namespace Dipping.WPF.Services
{
    public static class ImageFileSaver
    {
        public static string SaveGrab(VisionGrabResult grab, string folder, string cameraName, int? index = null)
        {
            if (grab.JpegBytes is { Length: > 0 } jpeg)
                return SaveJpegBytes(jpeg, folder, cameraName, index);
            var bitmap = grab.EnsureBitmap()
                ?? throw new InvalidOperationException("没有可保存的图像。");
            return SaveJpeg(bitmap, folder, cameraName, index);
        }

        public static string SaveJpegBytes(byte[] jpeg, string folder, string cameraName, int? index = null)
        {
            var path = NextPath(folder, cameraName, index);
            File.WriteAllBytes(path, jpeg);
            return path;
        }

        public static string SaveJpeg(BitmapSource bitmap, string folder, string cameraName, int? index = null)
        {
            var path = NextPath(folder, cameraName, index);
            if (bitmap.CanFreeze && !bitmap.IsFrozen)
                bitmap.Freeze();
            var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(path);
            encoder.Save(stream);
            return path;
        }

        private static string NextPath(string folder, string cameraName, int? index)
        {
            Directory.CreateDirectory(folder);
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            var name = Sanitize(cameraName);
            var file = index is int n
                ? $"{name}_{stamp}_{n:D3}.jpg"
                : $"{name}_{stamp}.jpg";
            return Path.Combine(folder, file);
        }

        public static string Sanitize(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "camera";
            var invalid = Path.GetInvalidFileNameChars();
            var chars = name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray();
            var cleaned = new string(chars).Trim();
            return string.IsNullOrWhiteSpace(cleaned) ? "camera" : cleaned;
        }
    }
}
