// Regenerates assets/SignalQuality.ico (the exe's icon in Explorer, Task Manager and the
// startup list) from the same drawing code the tray uses, plus the README preview image.
// Run tools\make-icon.cmd after changing IconFactory.
//   MakeIcon.exe <icon.ico> [preview.png]

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace SignalQuality
{
    static class MakeIcon
    {
        static int Main(string[] args)
        {
            string path = args.Length > 0 ? args[0] : Path.Combine("assets", "SignalQuality.ico");
            int[] sizes = { 16, 20, 24, 32, 48, 64, 128, 256 };

            var images = new List<byte[]>();
            foreach (int size in sizes)
            {
                using (Bitmap bmp = IconFactory.Render(Status.Green, Status.Unknown, size))
                using (var buffer = new MemoryStream())
                {
                    bmp.Save(buffer, ImageFormat.Png);   // PNG entries: supported by Windows Vista and later
                    images.Add(buffer.ToArray());
                }
            }

            string folder = Path.GetDirectoryName(Path.GetFullPath(path));
            if (folder.Length > 0) Directory.CreateDirectory(folder);

            using (FileStream file = File.Create(path))
            using (var w = new BinaryWriter(file))
            {
                w.Write((short)0);                  // reserved
                w.Write((short)1);                  // type: icon
                w.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    byte dimension = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);   // 0 means 256
                    w.Write(dimension);
                    w.Write(dimension);
                    w.Write((byte)0);               // colours in palette
                    w.Write((byte)0);               // reserved
                    w.Write((short)1);              // colour planes
                    w.Write((short)32);             // bits per pixel
                    w.Write(images[i].Length);
                    w.Write(offset);
                    offset += images[i].Length;
                }
                foreach (byte[] png in images) w.Write(png);
            }

            Console.WriteLine("Wrote " + path + " with " + sizes.Length + " sizes (16-256 px)");
            if (args.Length > 1) WritePreview(args[1]);
            return 0;
        }

        // A strip for the README: the three lights, a gap, then examples of each arrow.
        // Transparent background so it sits well on GitHub's light and dark themes.
        static void WritePreview(string path)
        {
            Status[,] lights =
            {
                { Status.Green, Status.Unknown }, { Status.Yellow, Status.Unknown }, { Status.Red, Status.Unknown },
                { Status.Green, Status.Yellow }, { Status.Yellow, Status.Green }, { Status.Yellow, Status.Red }, { Status.Red, Status.Green },
            };
            const int size = 64, gap = 20, groupGap = 56, pad = 8;
            int count = lights.GetLength(0);
            int width = 2 * pad + count * size + (count - 2) * gap + groupGap;

            using (var strip = new Bitmap(width, size + 2 * pad, PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(strip))
            {
                g.Clear(Color.Transparent);
                int x = pad;
                for (int i = 0; i < count; i++)
                {
                    using (Bitmap light = IconFactory.Render(lights[i, 0], lights[i, 1], size))
                        g.DrawImageUnscaled(light, x, pad);
                    x += size + (i == 2 ? groupGap : gap);
                }
                string folder = Path.GetDirectoryName(Path.GetFullPath(path));
                if (folder.Length > 0) Directory.CreateDirectory(folder);
                strip.Save(path, ImageFormat.Png);
            }
            Console.WriteLine("Wrote " + path);
        }
    }
}
