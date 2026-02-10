using System.Security.Cryptography;
using SkiaSharp;

namespace LyWaf.Services.Captcha;

/// <summary>
/// 验证码图片生成器
/// 使用 SkiaSharp 生成带干扰的验证码图片
/// </summary>
public static class CaptchaImageGenerator
{
    // 数学题图片尺寸
    private const int MathImageWidth = 280;
    private const int MathImageHeight = 70;

    // 深色调色板（用于字符颜色）
    private static readonly SKColor[] DarkColors =
    [
        new(30, 30, 100),   // 深蓝
        new(100, 20, 20),   // 深红
        new(20, 80, 20),    // 深绿
        new(80, 20, 80),    // 深紫
        new(20, 60, 80),    // 深青
        new(80, 60, 20),    // 深黄
    ];

    // 干扰线颜色
    private static readonly SKColor[] NoiseColors =
    [
        new(100, 100, 180, 160),
        new(180, 100, 100, 160),
        new(100, 180, 100, 160),
        new(150, 130, 80, 160),
        new(130, 80, 150, 160),
    ];

    /// <summary>
    /// 生成数学题验证码图片（带倾斜变形和干扰）
    /// </summary>
    /// <param name="questionText">数学表达式，如 "23 + 15 = ?"</param>
    /// <returns>base64 编码的 PNG 图片</returns>
    public static string GenerateMathImage(string questionText)
    {
        using var bitmap = new SKBitmap(MathImageWidth, MathImageHeight);
        using var canvas = new SKCanvas(bitmap);

        // 随机浅色背景
        var bgR = (byte)RandomNumberGenerator.GetInt32(220, 245);
        var bgG = (byte)RandomNumberGenerator.GetInt32(220, 245);
        var bgB = (byte)RandomNumberGenerator.GetInt32(225, 250);
        canvas.Clear(new SKColor(bgR, bgG, bgB));

        // 绘制干扰线（底层）
        DrawNoiseLines(canvas, MathImageWidth, MathImageHeight, 4 + RandomNumberGenerator.GetInt32(0, 3));

        // 绘制贝塞尔曲线干扰
        DrawBezierNoise(canvas, MathImageWidth, MathImageHeight, 2 + RandomNumberGenerator.GetInt32(0, 2));

        // 逐字符绘制（带倾斜变形）
        DrawDistortedText(canvas, questionText, MathImageWidth, MathImageHeight);

        // 绘制噪点（顶层）
        DrawNoiseDots(canvas, MathImageWidth, MathImageHeight, 30 + RandomNumberGenerator.GetInt32(0, 30));

        // 编码为 base64 PNG
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        return Convert.ToBase64String(data.ToArray());
    }

    /// <summary>
    /// 生成滑块验证背景图片（带干扰目标圆圈）
    /// </summary>
    /// <param name="realTargetPercent">真实目标位置百分比 (0-100)</param>
    /// <param name="width">图片宽度</param>
    /// <param name="height">图片高度</param>
    /// <returns>base64 编码的 PNG 图片</returns>
    public static string GenerateSliderBackgroundImage(int realTargetPercent, int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);

        // 透明背景
        canvas.Clear(SKColors.Transparent);

        // 绘制底层干扰线
        DrawSliderNoiseLines(canvas, width, height, 3 + RandomNumberGenerator.GetInt32(0, 3));

        // 绘制干扰虚线圆圈（假目标，避开真实目标区域）
        // 真实目标由 CSS .slider-target 元素绘制（更清晰、层级更高），背景图只画假目标作为干扰
        var decoyCount = 4 + RandomNumberGenerator.GetInt32(0, 5);
        var circleRadius = height / 2 - 4;
        var usedPositions = new List<int> { realTargetPercent };

        for (int i = 0; i < decoyCount; i++)
        {
            // 生成不与真实目标太近的位置
            int pos;
            int attempts = 0;
            do
            {
                pos = RandomNumberGenerator.GetInt32(8, 92);
                attempts++;
            }
            while (attempts < 50 && usedPositions.Any(p => Math.Abs(p - pos) < 10));

            if (attempts >= 50) continue;
            usedPositions.Add(pos);

            var cx = (float)(pos / 100.0 * (width - circleRadius * 2) + circleRadius);
            var cy = height / 2f;
            var alpha = (byte)RandomNumberGenerator.GetInt32(60, 120);
            var sizeVariation = RandomNumberGenerator.GetInt32(-2, 3);

            DrawDashedCircle(canvas, cx, cy, circleRadius + sizeVariation,
                new SKColor(99, 102, 241, alpha));
        }

        // 绘制噪点
        DrawNoiseDots(canvas, width, height, 15 + RandomNumberGenerator.GetInt32(0, 15));

        // 绘制小十字/菱形干扰
        DrawDecoyShapes(canvas, width, height, 3 + RandomNumberGenerator.GetInt32(0, 4));

        // 编码为 base64 PNG
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        return Convert.ToBase64String(data.ToArray());
    }

    #region 私有绘制方法

    /// <summary>
    /// 绘制倾斜变形的文字
    /// </summary>
    private static void DrawDistortedText(SKCanvas canvas, string text, int imgWidth, int imgHeight)
    {
        using var typeface = SKTypeface.FromFamilyName("Arial", SKFontStyleWeight.Bold,
            SKFontStyleWidth.Normal, SKFontStyleSlant.Upright) ?? SKTypeface.Default;

        // 先测量总宽度以居中
        float totalWidth = 0;
        var charInfos = new List<(char ch, float fontSize, float measuredWidth)>();

        foreach (var ch in text)
        {
            var fontSize = ch == ' ' ? 20f : 30f + RandomNumberGenerator.GetInt32(0, 9);
            using var font = new SKFont(typeface, fontSize);
            var w = font.MeasureText(ch.ToString());
            charInfos.Add((ch, fontSize, w));
            totalWidth += w + 2; // 2px 字间距
        }

        var startX = (imgWidth - totalWidth) / 2f;
        if (startX < 10) startX = 10;
        var baseY = imgHeight / 2f + 10; // 基线偏移

        float curX = startX;

        foreach (var (ch, fontSize, measuredWidth) in charInfos)
        {
            using var font = new SKFont(typeface, fontSize);
            font.Edging = SKFontEdging.Antialias;

            using var paint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Fill,
                Color = DarkColors[RandomNumberGenerator.GetInt32(0, DarkColors.Length)]
            };

            // 随机旋转角度
            var rotation = (float)(RandomNumberGenerator.GetInt32(-15, 16));
            // 随机 Y 偏移
            var yOffset = (float)(RandomNumberGenerator.GetInt32(-5, 6));

            canvas.Save();
            canvas.Translate(curX + measuredWidth / 2, baseY + yOffset);
            canvas.RotateDegrees(rotation);
            canvas.DrawText(ch.ToString(), -measuredWidth / 2, 0, font, paint);
            canvas.Restore();

            curX += measuredWidth + 2;
        }
    }

    /// <summary>
    /// 绘制干扰线
    /// </summary>
    private static void DrawNoiseLines(SKCanvas canvas, int width, int height, int count)
    {
        for (int i = 0; i < count; i++)
        {
            using var paint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1f + RandomNumberGenerator.GetInt32(0, 2),
                Color = NoiseColors[RandomNumberGenerator.GetInt32(0, NoiseColors.Length)]
            };

            var x1 = (float)RandomNumberGenerator.GetInt32(0, width);
            var y1 = (float)RandomNumberGenerator.GetInt32(0, height);
            var x2 = (float)RandomNumberGenerator.GetInt32(0, width);
            var y2 = (float)RandomNumberGenerator.GetInt32(0, height);

            canvas.DrawLine(x1, y1, x2, y2, paint);
        }
    }

    /// <summary>
    /// 绘制贝塞尔曲线干扰
    /// </summary>
    private static void DrawBezierNoise(SKCanvas canvas, int width, int height, int count)
    {
        for (int i = 0; i < count; i++)
        {
            using var paint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.5f,
                Color = NoiseColors[RandomNumberGenerator.GetInt32(0, NoiseColors.Length)]
            };

            using var path = new SKPath();
            var startX = (float)RandomNumberGenerator.GetInt32(0, width / 4);
            var startY = (float)RandomNumberGenerator.GetInt32(0, height);
            path.MoveTo(startX, startY);

            path.CubicTo(
                RandomNumberGenerator.GetInt32(width / 4, width / 2), RandomNumberGenerator.GetInt32(0, height),
                RandomNumberGenerator.GetInt32(width / 2, width * 3 / 4), RandomNumberGenerator.GetInt32(0, height),
                RandomNumberGenerator.GetInt32(width * 3 / 4, width), RandomNumberGenerator.GetInt32(0, height)
            );

            canvas.DrawPath(path, paint);
        }
    }

    /// <summary>
    /// 绘制噪点
    /// </summary>
    private static void DrawNoiseDots(SKCanvas canvas, int width, int height, int count)
    {
        for (int i = 0; i < count; i++)
        {
            using var paint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Fill,
                Color = new SKColor(
                    (byte)RandomNumberGenerator.GetInt32(0, 200),
                    (byte)RandomNumberGenerator.GetInt32(0, 200),
                    (byte)RandomNumberGenerator.GetInt32(0, 200),
                    (byte)RandomNumberGenerator.GetInt32(80, 200)
                )
            };

            var x = (float)RandomNumberGenerator.GetInt32(0, width);
            var y = (float)RandomNumberGenerator.GetInt32(0, height);
            var radius = 1f + RandomNumberGenerator.GetInt32(0, 2);

            canvas.DrawCircle(x, y, radius, paint);
        }
    }

    /// <summary>
    /// 绘制虚线圆圈
    /// </summary>
    private static void DrawDashedCircle(SKCanvas canvas, float cx, float cy, float radius, SKColor color)
    {
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2,
            Color = color,
            PathEffect = SKPathEffect.CreateDash([6, 4], 0)
        };

        canvas.DrawCircle(cx, cy, radius, paint);
    }

    /// <summary>
    /// 绘制滑块区域的干扰线
    /// </summary>
    private static void DrawSliderNoiseLines(SKCanvas canvas, int width, int height, int count)
    {
        for (int i = 0; i < count; i++)
        {
            using var paint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1f,
                Color = new SKColor(
                    (byte)RandomNumberGenerator.GetInt32(80, 160),
                    (byte)RandomNumberGenerator.GetInt32(80, 160),
                    (byte)RandomNumberGenerator.GetInt32(180, 255),
                    (byte)RandomNumberGenerator.GetInt32(40, 100)
                )
            };

            var x1 = (float)RandomNumberGenerator.GetInt32(0, width);
            var y1 = (float)RandomNumberGenerator.GetInt32(0, height);
            var x2 = (float)RandomNumberGenerator.GetInt32(0, width);
            var y2 = (float)RandomNumberGenerator.GetInt32(0, height);

            canvas.DrawLine(x1, y1, x2, y2, paint);
        }
    }

    /// <summary>
    /// 绘制小十字、菱形等干扰形状
    /// </summary>
    private static void DrawDecoyShapes(SKCanvas canvas, int width, int height, int count)
    {
        for (int i = 0; i < count; i++)
        {
            var cx = (float)RandomNumberGenerator.GetInt32(10, width - 10);
            var cy = (float)RandomNumberGenerator.GetInt32(4, height - 4);
            var alpha = (byte)RandomNumberGenerator.GetInt32(50, 120);

            using var paint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.5f,
                Color = new SKColor(99, 102, 241, alpha)
            };

            var shapeType = RandomNumberGenerator.GetInt32(0, 3);
            var size = 4f + RandomNumberGenerator.GetInt32(0, 4);

            switch (shapeType)
            {
                case 0: // 小十字
                    canvas.DrawLine(cx - size, cy, cx + size, cy, paint);
                    canvas.DrawLine(cx, cy - size, cx, cy + size, paint);
                    break;
                case 1: // 小菱形
                    using (var path = new SKPath())
                    {
                        path.MoveTo(cx, cy - size);
                        path.LineTo(cx + size, cy);
                        path.LineTo(cx, cy + size);
                        path.LineTo(cx - size, cy);
                        path.Close();
                        canvas.DrawPath(path, paint);
                    }
                    break;
                case 2: // 小方块
                    canvas.DrawRect(cx - size / 2, cy - size / 2, size, size, paint);
                    break;
            }
        }
    }

    #endregion
}
