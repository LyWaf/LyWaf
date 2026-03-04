using LyWaf.Plugins.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using NLog;
using SkiaSharp;

namespace LyWaf.Plugins.Sys;

/// <summary>
/// 系统插件：图片实时处理
/// 支持缩放、旋转、裁剪、格式转换
/// </summary>
public class ImageProcessPlugin : LyWafPluginBase
{
    private readonly PluginMetadata _metadata = new()
    {
        Id = "image-process",
        Name = "图片处理器",
        Version = "1.0.0",
        Description = "实时处理图片：缩放、旋转、裁剪、格式转换",
        Author = "LyWaf Team",
        Priority = PluginPriority.Normal,
        EnabledByDefault = false,  // 默认不启用
        DefaultOptions = new ImageProcessOptions()
    };

    public override PluginMetadata Metadata => _metadata;

    /// <summary>运行时配置，与 Metadata.DefaultOptions 是同一实例</summary>
    private ImageProcessOptions Options => (ImageProcessOptions)_metadata.DefaultOptions!;

    public override Task InitializeAsync(IPluginContext context)
    {
        base.InitializeAsync(context);
        context.Logger.Info("图片处理器已初始化，支持格式: {Formats}", string.Join(", ", Options.SupportedFormats));
        return Task.CompletedTask;
    }

    public override void ConfigureProxyPipeline(IApplicationBuilder proxyApp)
    {
        proxyApp.UseMiddleware<ImageProcessMiddleware>();
    }
}

/// <summary>
/// 图片处理配置选项
/// </summary>
public class ImageProcessOptions
{
    /// <summary>是否启用</summary>
    [ConfigurationKeyName("Enabled")]
    [System.ComponentModel.Description("启用")]
    public bool Enabled { get; set; } = true;

    // 别名：支持 Enable 作为 Enabled 的别名
    [ConfigurationKeyName("Enable")]
    public bool? EnableAlias { set => Enabled = value ?? Enabled; }

    /// <summary>支持处理的图片格式</summary>
    [ConfigurationKeyName("SupportedFormats")]
    [System.ComponentModel.Description("支持的图片格式")]
    public List<string> SupportedFormats { get; set; } = ["jpg", "jpeg", "png", "webp", "gif", "bmp"];

    // 别名：支持 Formats 作为 SupportedFormats 的别名
    [ConfigurationKeyName("Formats")]
    public List<string>? FormatsAlias { set => SupportedFormats = value ?? SupportedFormats; }

    /// <summary>默认输出格式</summary>
    [ConfigurationKeyName("DefaultOutputFormat")]
    [System.ComponentModel.Description("默认输出格式")]
    public string DefaultOutputFormat { get; set; } = "webp";

    // 别名：支持 Format 和 OutputFormat
    [ConfigurationKeyName("Format")]
    public string? FormatAlias { set => DefaultOutputFormat = value ?? DefaultOutputFormat; }

    [ConfigurationKeyName("OutputFormat")]
    public string? OutputFormatAlias { set => DefaultOutputFormat = value ?? DefaultOutputFormat; }

    /// <summary>默认输出质量 (1-100)</summary>
    [ConfigurationKeyName("DefaultQuality")]
    [System.ComponentModel.Description("默认质量 (1-100)")]
    public int DefaultQuality { get; set; } = 85;

    // 别名：支持 Quality
    [ConfigurationKeyName("Quality")]
    public int? QualityAlias { set => DefaultQuality = value ?? DefaultQuality; }

    /// <summary>最大输出宽度</summary>
    [ConfigurationKeyName("MaxWidth")]
    [System.ComponentModel.Description("最大宽度")]
    public int MaxWidth { get; set; } = 4096;

    /// <summary>最大输出高度</summary>
    [ConfigurationKeyName("MaxHeight")]
    [System.ComponentModel.Description("最大高度")]
    public int MaxHeight { get; set; } = 4096;

    /// <summary>启用 URL 参数处理</summary>
    [ConfigurationKeyName("EnableUrlParams")]
    [System.ComponentModel.Description("启用URL参数")]
    public bool EnableUrlParams { get; set; } = true;

    /// <summary>缓存处理后的图片（秒）</summary>
    [ConfigurationKeyName("CacheSeconds")]
    [System.ComponentModel.Description("缓存时长(秒)")]
    public int CacheSeconds { get; set; } = 3600;

    // 别名：支持 Cache
    [ConfigurationKeyName("Cache")]
    public int? CacheAlias { set => CacheSeconds = value ?? CacheSeconds; }
}

/// <summary>
/// 图片处理中间件
/// URL 参数说明：
/// - w: 宽度 (width)
/// - h: 高度 (height)
/// - q: 质量 (quality, 1-100)
/// - f: 格式 (format: jpg, png, webp, gif)
/// - r: 旋转角度 (rotate: 90, 180, 270)
/// - c: 裁剪 (crop: x,y,w,h)
/// - fit: 适应模式 (contain, cover, fill)
/// 
/// 示例：/image.jpg?w=200&h=200&f=webp&q=80
/// </summary>
public class ImageProcessMiddleware(RequestDelegate next, IConfiguration configuration)
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
    private readonly RequestDelegate _next = next;
    private readonly ImageProcessOptions _options = GetOptions(configuration);

    private static ImageProcessOptions GetOptions(IConfiguration configuration)
    {
        var options = new ImageProcessOptions();
        configuration.GetSection("Plugins:image-process").Bind(options);
        return options;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // 检查是否需要处理图片
        if (!ShouldProcessImage(context))
        {
            await _next(context);
            return;
        }

        // 获取处理参数
        var processParams = ParseParams(context.Request.Query, context.Request.Path.Value);
        if (!processParams.HasAnyTransform)
        {
            await _next(context);
            return;
        }

        // 捕获响应
        var originalBody = context.Response.Body;
        using var memoryStream = new MemoryStream();
        context.Response.Body = memoryStream;

        await _next(context);

        // 检查响应是否是图片
        if (!IsImageResponse(context.Response))
        {
            memoryStream.Position = 0;
            await memoryStream.CopyToAsync(originalBody);
            context.Response.Body = originalBody;
            return;
        }

        try
        {
            memoryStream.Position = 0;
            var processedImage = await ProcessImageAsync(memoryStream, processParams);

            // 设置响应头
            context.Response.Body = originalBody;
            context.Response.ContentType = GetContentType(processParams.Format ?? _options.DefaultOutputFormat);
            context.Response.ContentLength = processedImage.Length;

            if (_options.CacheSeconds > 0)
            {
                context.Response.Headers.CacheControl = $"public, max-age={_options.CacheSeconds}";
            }

            await originalBody.WriteAsync(processedImage);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "图片处理失败: {Path}", context.Request.Path);
            // 返回原始图片
            context.Response.Body = originalBody;
            memoryStream.Position = 0;
            await memoryStream.CopyToAsync(originalBody);
        }
    }

    private bool ShouldProcessImage(HttpContext context)
    {
        if (!_options.Enabled || !_options.EnableUrlParams)
            return false;

        var path = context.Request.Path.Value?.ToLower() ?? "";
        var extension = Path.GetExtension(path).TrimStart('.');

        return _options.SupportedFormats.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsImageResponse(HttpResponse response)
    {
        var contentType = response.ContentType?.ToLower() ?? "";
        return contentType.StartsWith("image/");
    }

    private ImageProcessParams ParseParams(IQueryCollection query, string? path)
    {
        var p = new ImageProcessParams();

        // 宽度
        if (query.TryGetValue("w", out var w) && int.TryParse(w, out var width))
            p.Width = Math.Min(width, _options.MaxWidth);

        // 高度
        if (query.TryGetValue("h", out var h) && int.TryParse(h, out var height))
            p.Height = Math.Min(height, _options.MaxHeight);

        // 质量
        if (query.TryGetValue("q", out var q) && int.TryParse(q, out var quality))
            p.Quality = Math.Clamp(quality, 1, 100);
        else
            p.Quality = _options.DefaultQuality;

        // 格式
        if (query.TryGetValue("f", out var f))
        {
            var format = f.ToString().ToLower();
            if (_options.SupportedFormats.Contains(format, StringComparer.OrdinalIgnoreCase))
                p.Format = format;
        }
        else
        {
            // 如果没有指定格式，使用原图格式（如果是支持的格式）
            var originalExt = Path.GetExtension(path ?? "").TrimStart('.').ToLower();
            if (!string.IsNullOrEmpty(originalExt) && 
                _options.SupportedFormats.Contains(originalExt, StringComparer.OrdinalIgnoreCase))
            {
                p.Format = originalExt;
            }
        }

        // 旋转
        if (query.TryGetValue("r", out var r) && int.TryParse(r, out var rotate))
            p.Rotate = rotate % 360;

        // 裁剪 (x,y,w,h)
        if (query.TryGetValue("c", out var c))
        {
            var parts = c.ToString().Split(',');
            if (parts.Length == 4 &&
                int.TryParse(parts[0], out var cx) &&
                int.TryParse(parts[1], out var cy) &&
                int.TryParse(parts[2], out var cw) &&
                int.TryParse(parts[3], out var ch))
            {
                p.CropX = cx;
                p.CropY = cy;
                p.CropWidth = cw;
                p.CropHeight = ch;
            }
        }

        // 适应模式
        if (query.TryGetValue("fit", out var fit))
            p.FitMode = fit.ToString().ToLower();

        return p;
    }

    private async Task<byte[]> ProcessImageAsync(Stream inputStream, ImageProcessParams p)
    {
        using var original = SKBitmap.Decode(inputStream) ?? throw new InvalidOperationException("无法解码图片");
        SKBitmap current = original;
        bool needsDispose = false;

        try
        {
            // 1. 裁剪
            if (p.HasCrop)
            {
                var cropRect = new SKRectI(
                    Math.Max(0, p.CropX),
                    Math.Max(0, p.CropY),
                    Math.Min(current.Width, p.CropX + p.CropWidth),
                    Math.Min(current.Height, p.CropY + p.CropHeight)
                );

                var cropped = new SKBitmap(cropRect.Width, cropRect.Height);
                using var canvas = new SKCanvas(cropped);
                canvas.DrawBitmap(current, cropRect, new SKRect(0, 0, cropRect.Width, cropRect.Height));

                if (needsDispose) current.Dispose();
                current = cropped;
                needsDispose = true;
            }

            // 2. 旋转
            if (p.Rotate != 0)
            {
                var rotated = RotateBitmap(current, p.Rotate);
                if (needsDispose) current.Dispose();
                current = rotated;
                needsDispose = true;
            }

            // 3. 缩放
            if (p.HasResize)
            {
                var (newWidth, newHeight) = CalculateSize(current.Width, current.Height, p);
                var resized = current.Resize(new SKImageInfo(newWidth, newHeight), SKSamplingOptions.Default);
                if (resized != null)
                {
                    if (needsDispose) current.Dispose();
                    current = resized;
                    needsDispose = true;
                }
            }

            // 4. 编码输出
            using var image = SKImage.FromBitmap(current);
            var format = GetSkiaFormat(p.Format ?? _options.DefaultOutputFormat);
            using var data = image.Encode(format, p.Quality);

            return data.ToArray();
        }
        finally
        {
            if (needsDispose && current != original)
                current.Dispose();
        }
    }

    private static SKBitmap RotateBitmap(SKBitmap source, int degrees)
    {
        var radians = degrees * Math.PI / 180;
        var sin = Math.Abs(Math.Sin(radians));
        var cos = Math.Abs(Math.Cos(radians));

        var newWidth = (int)(source.Width * cos + source.Height * sin);
        var newHeight = (int)(source.Width * sin + source.Height * cos);

        var rotated = new SKBitmap(newWidth, newHeight);
        using var canvas = new SKCanvas(rotated);

        canvas.Translate(newWidth / 2f, newHeight / 2f);
        canvas.RotateDegrees(degrees);
        canvas.Translate(-source.Width / 2f, -source.Height / 2f);
        canvas.DrawBitmap(source, 0, 0);

        return rotated;
    }

    private static (int width, int height) CalculateSize(int originalWidth, int originalHeight, ImageProcessParams p)
    {
        int newWidth = p.Width ?? originalWidth;
        int newHeight = p.Height ?? originalHeight;

        // 如果只指定了一个维度，按比例计算另一个
        if (p.Width.HasValue && !p.Height.HasValue)
        {
            newHeight = (int)(originalHeight * ((float)p.Width.Value / originalWidth));
        }
        else if (!p.Width.HasValue && p.Height.HasValue)
        {
            newWidth = (int)(originalWidth * ((float)p.Height.Value / originalHeight));
        }
        else if (p.Width.HasValue && p.Height.HasValue)
        {
            switch (p.FitMode)
            {
                case "contain":
                    // 保持比例，适应指定尺寸内
                    var scaleContain = Math.Min((float)p.Width.Value / originalWidth, (float)p.Height.Value / originalHeight);
                    newWidth = (int)(originalWidth * scaleContain);
                    newHeight = (int)(originalHeight * scaleContain);
                    break;

                case "cover":
                    // 保持比例，覆盖指定尺寸
                    var scaleCover = Math.Max((float)p.Width.Value / originalWidth, (float)p.Height.Value / originalHeight);
                    newWidth = (int)(originalWidth * scaleCover);
                    newHeight = (int)(originalHeight * scaleCover);
                    break;

                case "fill":
                default:
                    // 拉伸填充
                    newWidth = p.Width.Value;
                    newHeight = p.Height.Value;
                    break;
            }
        }

        return (Math.Max(1, newWidth), Math.Max(1, newHeight));
    }

    private static SKEncodedImageFormat GetSkiaFormat(string format)
    {
        return format.ToLower() switch
        {
            "jpg" or "jpeg" => SKEncodedImageFormat.Jpeg,
            "png" => SKEncodedImageFormat.Png,
            "webp" => SKEncodedImageFormat.Webp,
            "gif" => SKEncodedImageFormat.Gif,
            "bmp" => SKEncodedImageFormat.Bmp,
            "ico" => SKEncodedImageFormat.Ico,
            _ => SKEncodedImageFormat.Webp
        };
    }

    private static string GetContentType(string format)
    {
        return format.ToLower() switch
        {
            "jpg" or "jpeg" => "image/jpeg",
            "png" => "image/png",
            "webp" => "image/webp",
            "gif" => "image/gif",
            "bmp" => "image/bmp",
            "ico" => "image/x-icon",
            _ => "image/webp"
        };
    }
}

/// <summary>
/// 图片处理参数
/// </summary>
public class ImageProcessParams
{
    public int? Width { get; set; }
    public int? Height { get; set; }
    public int Quality { get; set; } = 85;
    public string? Format { get; set; }
    public int Rotate { get; set; }
    public int CropX { get; set; }
    public int CropY { get; set; }
    public int CropWidth { get; set; }
    public int CropHeight { get; set; }
    public string? FitMode { get; set; }

    public bool HasResize => Width.HasValue || Height.HasValue;
    public bool HasCrop => CropWidth > 0 && CropHeight > 0;
    public bool HasAnyTransform => HasResize || HasCrop || Rotate != 0 || Format != null;
}
