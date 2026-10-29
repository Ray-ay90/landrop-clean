using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class Program
{
    private static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
    private const string MarkPath = "M64,72 L92,72 L128,164 L164,72 L192,72 L143,188 L113,188 Z";

    [STAThread]
    private static int Main()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var assets = Path.Combine(root, "src/Assets");
        var preview = Path.Combine(root, "design/codex-layout");
        Directory.CreateDirectory(assets);
        Directory.CreateDirectory(preview);
        var frames = Sizes.Select(size => (Size: size, Png: Render(size))).ToArray();
        using (var file = File.Create(Path.Combine(assets, "app.ico")))
        using (var writer = new BinaryWriter(file))
        {
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)frames.Length);
            var offset = 6 + frames.Length * 16;
            foreach (var frame in frames)
            {
                writer.Write((byte)(frame.Size == 256 ? 0 : frame.Size));
                writer.Write((byte)(frame.Size == 256 ? 0 : frame.Size));
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((ushort)1);
                writer.Write((ushort)32);
                writer.Write(frame.Png.Length);
                writer.Write(offset);
                offset += frame.Png.Length;
            }
            foreach (var frame in frames) writer.Write(frame.Png);
        }
        foreach (var size in new[] { 16, 32, 256 })
            File.WriteAllBytes(Path.Combine(preview, $"brand-v-{size}.png"), frames.Single(frame => frame.Size == size).Png);
        File.WriteAllBytes(Path.Combine(assets, "brand-v.png"), frames[^1].Png);
        File.WriteAllText(Path.Combine(assets, "brand-v.svg"),
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"256\" height=\"256\" viewBox=\"0 0 256 256\"><rect x=\"16\" y=\"16\" width=\"224\" height=\"224\" rx=\"42\" fill=\"#202124\"/><path d=\"{MarkPath}\" fill=\"#FAFAFA\"/></svg>\n");
        var validation = Validate(Path.Combine(assets, "app.ico"));
        File.WriteAllText(Path.Combine(preview, "brand-v-validation.json"), JsonSerializer.Serialize(validation, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Generated app.ico ({frames.Length} PNG frames) and brand-v.png; validated: {string.Join(", ", validation)}");
        return 0;
    }

    private static byte[] Render(int size)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(size / 256.0, size / 256.0));
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x20, 0x21, 0x24)), null, new Rect(16, 16, 224, 224), 42, 42);
            dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFA)), null, Geometry.Parse(MarkPath));
            dc.Pop();
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static int[] Validate(string path)
    {
        var bytes = File.ReadAllBytes(path);
        using var reader = new BinaryReader(new MemoryStream(bytes));
        if (reader.ReadUInt16() != 0 || reader.ReadUInt16() != 1 || reader.ReadUInt16() != Sizes.Length)
            throw new InvalidDataException("Unexpected ICO header.");
        foreach (var size in Sizes)
        {
            var widthByte = reader.ReadByte();
            var heightByte = reader.ReadByte();
            var width = widthByte == 0 ? 256 : widthByte;
            var height = heightByte == 0 ? 256 : heightByte;
            reader.ReadUInt16();
            if (reader.ReadUInt16() != 1 || reader.ReadUInt16() != 32) throw new InvalidDataException("Unexpected frame format.");
            var length = reader.ReadInt32();
            var offset = reader.ReadInt32();
            if (width != size || height != size || offset < 6 + Sizes.Length * 16 || offset + length > bytes.Length)
                throw new InvalidDataException("Invalid ICO frame directory.");
            using var stream = new MemoryStream(bytes, offset, length);
            var decoded = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            if (decoded.PixelWidth != size || decoded.PixelHeight != size) throw new InvalidDataException("Decoded frame size differs.");
        }
        return Sizes;
    }
}
