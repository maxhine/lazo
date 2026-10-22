using System;
using System.IO;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Lazo
{
    internal static class ProfilePhoto
    {
        public static string Current = "";
        private static string FilePath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lazo", "profile.jpg"); } }
        public static void Load()
        {
            try { byte[] bytes = File.ReadAllBytes(FilePath); Current = bytes.Length <= 12000 ? Convert.ToBase64String(bytes) : ""; }
            catch { Current = ""; }
        }
        public static void Set(string path, bool persist)
        {
            byte[] bytes = null;
            if (path != null)
            {
                if (new FileInfo(path).Length > 20 * 1024 * 1024) throw new InvalidOperationException("Elige una imagen de menos de 20 MB.");
                using (Image source = Image.FromFile(path))
                using (Bitmap thumb = new Bitmap(96, 96))
                using (Graphics graphics = Graphics.FromImage(thumb))
                using (MemoryStream output = new MemoryStream())
                {
                    graphics.Clear(Color.FromArgb(230, 230, 230));
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    int side = Math.Min(source.Width, source.Height);
                    using (ImageAttributes attributes = new ImageAttributes())
                    {
                        attributes.SetColorMatrix(new ColorMatrix(new float[][] {
                            new float[] {.299f,.299f,.299f,0,0}, new float[] {.587f,.587f,.587f,0,0},
                            new float[] {.114f,.114f,.114f,0,0}, new float[] {0,0,0,1,0}, new float[] {0,0,0,0,1} }));
                        graphics.DrawImage(source, new Rectangle(0,0,96,96), (source.Width-side)/2, (source.Height-side)/2,
                            side, side, GraphicsUnit.Pixel, attributes);
                    }
                    thumb.Save(output, ImageFormat.Jpeg);
                    bytes = output.ToArray();
                }
                if (bytes.Length > 12000) throw new InvalidOperationException("No se pudo reducir la imagen.");
            }
            if (persist)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                if (bytes == null) { if (File.Exists(FilePath)) File.Delete(FilePath); }
                else File.WriteAllBytes(FilePath, bytes);
            }
            Current = bytes == null ? "" : Convert.ToBase64String(bytes);
        }
    }
}
