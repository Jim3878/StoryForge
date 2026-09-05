using System.Drawing;
using System.Drawing.Imaging;

namespace StoryForge.Core.Portrait;

// Mirrors the original WinForms tool's PictureBox crop exactly: only the top/bottom get cut away (by
// percentage of the source image's own height), the full width is always kept. Percentages come from
// AppSettings.PortraitTopCutPercent/PortraitBottomCutPercent.
public static class PortraitThumbnailRenderer
{
    public static byte[] RenderCroppedPng(string filePath, float topCutPercent, float bottomCutPercent)
    {
        // Read bytes first rather than handing the file path straight to Image.FromFile — that keeps the
        // file handle open for the lifetime of the Image, which can collide with Unity/the LDtk editor
        // having the same file open. Wrapping the byte buffer in a MemoryStream avoids that lock entirely.
        var bytes = File.ReadAllBytes(filePath);
        using var stream = new MemoryStream(bytes);
        using var source = Image.FromStream(stream);

        var topRatio = Math.Clamp(topCutPercent / 100f, 0f, 1f);
        var bottomRatio = Math.Clamp(bottomCutPercent / 100f, 0f, 1f);

        var height = source.Height;
        var width = source.Width;

        var cropTop = Math.Clamp((int)(height * topRatio), 0, height - 1);
        var cropBottom = Math.Clamp((int)(height * bottomRatio), 0, height - 1 - cropTop);
        var cropHeight = Math.Max(1, height - cropTop - cropBottom);
        var cropRect = new Rectangle(0, cropTop, width, cropHeight);

        using var cropped = new Bitmap(cropRect.Width, cropRect.Height);
        using var g = Graphics.FromImage(cropped);
        g.DrawImage(source, new Rectangle(0, 0, cropRect.Width, cropRect.Height), cropRect, GraphicsUnit.Pixel);

        using var output = new MemoryStream();
        cropped.Save(output, ImageFormat.Png);
        return output.ToArray();
    }
}
