using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace StoryForge.Core.Cg;

// 暫代圖：找不到美術檔的 CG 產一組一看就知道是假的圖（移植自 Unity CgPlaceholderRenderer）。
// 色相代表 CG（依全部 CG 自然排序後 × 黃金角），差分在同色系由亮到暗、斜紋方向交替，底部分段條亮第幾格。
// 卡片預覽、512x288 縮圖、1920x1080 正式暫代圖都畫同一張 1920x1080 再縮放，三者長得一樣。
public static class CgPlaceholderRenderer
{
    public const string DefaultDiffId = "01";
    private const int Width = 1920;
    private const int Height = 1080;
    private const float HueStart = 20f;
    private const float HueStep = 137.5f;
    private const float Saturation = 55f;
    private const float BaseLightness = 48f;
    private const int Frame = 40;
    private const float LightnessSpan = 18f;
    private const int StripeWidth = 60;
    private const float SegmentBarHeight = 28f;
    private static readonly string[] FontNames = { "Microsoft JhengHei", "微軟正黑體", "Arial" };

    public static float GetHue(int sortedIndex) => (HueStart + sortedIndex * HueStep) % 360f;

    public static byte[] RenderPng(string cgId, string diffId, int diffIndex, int diffCount, float hue, string memo, int width, int height)
    {
        using var full = Render(cgId, diffId, diffIndex, diffCount, hue, memo);
        using var output = width == Width && height == Height ? new Bitmap(full) : Scale(full, width, height);
        using var stream = new MemoryStream();
        output.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private static Bitmap Render(string cgId, string diffId, int diffIndex, int diffCount, float hue, string memo)
    {
        diffCount = Math.Max(1, diffCount);
        var lightness = diffCount <= 1 ? BaseLightness : BaseLightness + LightnessSpan / 2f - LightnessSpan * diffIndex / (diffCount - 1);
        var baseColor = Hsl(hue, Saturation, lightness);
        var stripeColor = Hsl(hue, Saturation, lightness - 10f);
        var frameColor = Hsl(hue, Saturation, BaseLightness - 28f);

        var bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(frameColor);

        var inner = new Rectangle(Frame, Frame, Width - Frame * 2, Height - Frame * 2);
        using (var background = new SolidBrush(baseColor))
            g.FillRectangle(background, inner);

        var state = g.Save();
        g.SetClip(inner);
        using (var stripe = new Pen(stripeColor, StripeWidth / 2f))
        {
            var forward = diffIndex % 2 == 0;
            for (var x = -Height; x < Width + Height; x += StripeWidth)
            {
                if (forward)
                    g.DrawLine(stripe, x, Height, x + Height, 0);
                else
                    g.DrawLine(stripe, x, 0, x + Height, Height);
            }
        }
        g.Restore(state);

        if (diffCount > 1)
        {
            var barTop = Height - Frame - SegmentBarHeight - 12f;
            var segmentWidth = (Width - Frame * 2 - 24f) / diffCount;
            for (var i = 0; i < diffCount; i++)
            {
                using var brush = new SolidBrush(Color.FromArgb(i == diffIndex ? 230 : 70, 255, 255, 255));
                g.FillRectangle(brush, Frame + 12f + i * segmentWidth + 3f, barTop, segmentWidth - 6f, SegmentBarHeight);
            }
        }

        DrawTextBlock(g, cgId, $"diff: {diffId}  ({diffIndex + 1}/{diffCount})", memo);
        return bitmap;
    }

    private static void DrawTextBlock(Graphics g, string name, string diff, string memo)
    {
        using var nameFont = CreateFont(110f, FontStyle.Bold);
        using var diffFont = CreateFont(52f, FontStyle.Regular);
        using var memoFont = CreateFont(38f, FontStyle.Regular);
        var maxWidth = Width * 0.7f;
        var nameSize = g.MeasureString(name, nameFont);
        var diffSize = g.MeasureString(diff, diffFont);
        var memoSize = string.IsNullOrWhiteSpace(memo) ? SizeF.Empty : g.MeasureString(memo.Trim(), memoFont, (int)maxWidth);
        var gap = 24f;
        var blockWidth = Math.Max(nameSize.Width, Math.Max(diffSize.Width, memoSize.Width));
        var blockHeight = nameSize.Height + gap + diffSize.Height + (memoSize.IsEmpty ? 0 : gap + memoSize.Height);
        var top = (Height - blockHeight) / 2f;

        var plate = new RectangleF((Width - blockWidth) / 2f - 48f, top - 36f, blockWidth + 96f, blockHeight + 72f);
        using (var plateBrush = new SolidBrush(Color.FromArgb(140, 0, 0, 0)))
            g.FillRectangle(plateBrush, plate);

        using var white = new SolidBrush(Color.White);
        using var soft = new SolidBrush(Color.FromArgb(220, 255, 255, 255));
        var centered = new StringFormat { Alignment = StringAlignment.Center };
        g.DrawString(name, nameFont, white, new RectangleF(0, top, Width, nameSize.Height), centered);
        top += nameSize.Height + gap;
        g.DrawString(diff, diffFont, soft, new RectangleF(0, top, Width, diffSize.Height), centered);
        if (!memoSize.IsEmpty)
        {
            top += diffSize.Height + gap;
            g.DrawString(memo.Trim(), memoFont, soft, new RectangleF((Width - maxWidth) / 2f, top, maxWidth, memoSize.Height + 4f), centered);
        }
    }

    private static Font CreateFont(float size, FontStyle style)
    {
        using var installed = new InstalledFontCollection();
        var family = FontNames.FirstOrDefault(name => installed.Families.Any(f => f.Name == name)) ?? FontFamily.GenericSansSerif.Name;
        return new Font(family, size, style, GraphicsUnit.Pixel);
    }

    private static Bitmap Scale(Image source, int width, int height)
    {
        var output = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(output);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(source, new Rectangle(0, 0, width, height));
        return output;
    }

    private static Color Hsl(float hue, float saturation, float lightness)
    {
        var s = saturation / 100f;
        var l = Math.Clamp(lightness / 100f, 0f, 1f);
        var c = (1f - Math.Abs(2f * l - 1f)) * s;
        var hp = hue / 60f;
        var x = c * (1f - Math.Abs(hp % 2f - 1f));
        var (r, g, b) = hp switch
        {
            < 1 => (c, x, 0f),
            < 2 => (x, c, 0f),
            < 3 => (0f, c, x),
            < 4 => (0f, x, c),
            < 5 => (x, 0f, c),
            _ => (c, 0f, x)
        };
        var m = l - c / 2f;
        return Color.FromArgb(255, (int)((r + m) * 255), (int)((g + m) * 255), (int)((b + m) * 255));
    }
}

// 把任意圖縮成指定大小的 16:9 縮圖（比例不符置中補黑邊），給 png 來源產遊戲內縮圖用。
public static class CgImageUtility
{
    public static void SaveLetterboxed(string sourcePath, string outputPath, int width, int height)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllBytes(outputPath, RenderLetterboxed(sourcePath, width, height));
    }

    public static byte[] RenderLetterboxed(string sourcePath, int width, int height)
    {
        // 先讀成 bytes，不讓 Image 一直握著檔案（美術可能正開著同一個檔）。
        var bytes = File.ReadAllBytes(sourcePath);
        using var stream = new MemoryStream(bytes);
        using var source = Image.FromStream(stream);
        using var output = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(output))
        {
            g.Clear(Color.Black);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            var scale = Math.Min((float)width / source.Width, (float)height / source.Height);
            var w = source.Width * scale;
            var h = source.Height * scale;
            g.DrawImage(source, (width - w) / 2f, (height - h) / 2f, w, h);
        }

        using var result = new MemoryStream();
        output.Save(result, ImageFormat.Png);
        return result.ToArray();
    }
}
