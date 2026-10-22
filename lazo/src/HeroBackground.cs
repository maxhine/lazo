using System;
using System.IO;
using System.Windows.Media.Imaging;

namespace Lazo
{
    /// <summary>Imagen opcional de fondo de la tarjeta de inicio, guardada en %AppData%\Lazo\hero.jpg.</summary>
    internal static class HeroBackground
    {
        private const int MaxWidth = 1600;
        private static BitmapSource _cache;
        private static DateTime _cacheStamp;

        private static string FilePath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lazo", "hero.jpg");
        }

        public static bool IsCustom { get { return File.Exists(FilePath()); } }

        public static BitmapSource Load()
        {
            string path = FilePath();
            try
            {
                if (!File.Exists(path)) { _cache = null; return null; }
                DateTime stamp = File.GetLastWriteTimeUtc(path);
                if (_cache != null && stamp == _cacheStamp) return _cache;
                BitmapImage image = new BitmapImage();
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.StreamSource = stream;
                    image.EndInit();
                }
                image.Freeze();
                _cache = image;
                _cacheStamp = stamp;
                return image;
            }
            catch { return null; }
        }

        /// <summary>Copia la imagen elegida, reducida a un ancho razonable, para no depender del archivo original.</summary>
        public static void Set(string source)
        {
            BitmapImage original = new BitmapImage();
            using (FileStream input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                original.BeginInit();
                original.CacheOption = BitmapCacheOption.OnLoad;
                original.StreamSource = input;
                original.EndInit();
            }
            BitmapSource frame = original;
            if (original.PixelWidth > MaxWidth)
            {
                double scale = (double)MaxWidth / original.PixelWidth;
                frame = new TransformedBitmap(original, new System.Windows.Media.ScaleTransform(scale, scale));
            }
            if (frame.Format != System.Windows.Media.PixelFormats.Bgr24 && frame.Format != System.Windows.Media.PixelFormats.Bgr32)
                frame = new FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Bgr32, null, 0);
            string path = FilePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".tmp";
            JpegBitmapEncoder encoder = new JpegBitmapEncoder { QualityLevel = 88 };
            encoder.Frames.Add(BitmapFrame.Create(frame));
            using (FileStream output = File.Create(temp)) encoder.Save(output);
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
            _cache = null;
        }

        public static void Clear()
        {
            try { File.Delete(FilePath()); } catch { }
            _cache = null;
        }
    }
}
