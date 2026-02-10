using System.Text.Json;
using LyWaf.Services.Captcha;
using LyWaf.Shared;
using LyWaf.Utils;
using NLog;

namespace LyWaf.Middleware;

/// <summary>
/// 验证码中间件
/// 拦截需要验证码的 IP 请求，展示验证码页面或处理验证 API
/// </summary>
public class CaptchaMiddleware(RequestDelegate next, ICaptchaService captchaService)
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
    private readonly RequestDelegate _next = next;
    private readonly ICaptchaService _captchaService = captchaService;

    private const string CaptchaApiPrefix = "/__captcha/";

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";
        var clientIp = RequestUtil.GetClientIp(context.Request);

        // 处理验证码 API 请求
        if (path.StartsWith(CaptchaApiPrefix, StringComparison.OrdinalIgnoreCase))
        {
            await HandleCaptchaApi(context, clientIp, path);
            return;
        }

        // 检查 IP 是否需要验证码
        if (!_captchaService.IsPending(clientIp))
        {
            await _next(context);
            return;
        }

        // 需要验证码 - 返回验证码页面
        await WriteCaptchaPage(context, clientIp);
    }

    private async Task HandleCaptchaApi(HttpContext context, string clientIp, string path)
    {
        var action = path[CaptchaApiPrefix.Length..].TrimEnd('/');

        switch (action.ToLowerInvariant())
        {
            case "challenge":
                await HandleGetChallenge(context, clientIp);
                break;
            case "verify":
                await HandleVerify(context, clientIp);
                break;
            default:
                context.Response.StatusCode = 404;
                await context.Response.WriteAsync("Not Found");
                break;
        }
    }

    private async Task HandleGetChallenge(HttpContext context, string clientIp)
    {
        var challenge = _captchaService.GenerateChallenge(clientIp);

        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            success = true,
            token = challenge.Token,
            mode = challenge.Mode,
            question = challenge.Question,
            sliderTarget = challenge.SliderTarget
        }));
    }

    private async Task HandleVerify(HttpContext context, string clientIp)
    {
        context.Response.ContentType = "application/json; charset=utf-8";

        try
        {
            var body = await JsonSerializer.DeserializeAsync<CaptchaVerifyRequest>(
                context.Request.Body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (body == null)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { success = false, message = "请求体为空" }));
                return;
            }

            var behavior = body.Behavior != null
                ? new BehaviorData
                {
                    MouseTrackCount = body.Behavior.MouseTrackCount,
                    Duration = body.Behavior.Duration,
                    SliderOffset = body.Behavior.SliderOffset
                }
                : null;

            var result = _captchaService.Verify(clientIp, body.Token ?? "", body.Answer ?? "", behavior);

            if (result.Success)
            {
                await context.Response.WriteAsync(JsonSerializer.Serialize(new
                {
                    success = true,
                    message = result.Message
                }));
            }
            else
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(JsonSerializer.Serialize(new
                {
                    success = false,
                    message = result.Message
                }));
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "验证码验证异常: IP={Ip}", clientIp);
            context.Response.StatusCode = 500;
            await context.Response.WriteAsync(JsonSerializer.Serialize(new { success = false, message = "验证失败" }));
        }
    }

    private static async Task WriteCaptchaPage(HttpContext context, string clientIp)
    {
        if (context.Response.HasStarted) return;

        context.Response.StatusCode = 403;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";

        var html = GetCaptchaPageHtml(clientIp);
        await context.Response.WriteAsync(html);
    }

    private static string GetCaptchaPageHtml(string clientIp)
    {
        return """
<!DOCTYPE html>
<html lang="zh-CN">
<head>
    <meta charset="utf-8">
    <meta name="viewport" content="width=device-width, initial-scale=1">
    <title>安全验证 - LyWaf</title>
    <style>
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body {
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
            background: linear-gradient(135deg, #1a1a2e 0%, #16213e 100%);
            min-height: 100vh;
            display: flex;
            align-items: center;
            justify-content: center;
            color: #e4e4e7;
        }
        .container {
            background: rgba(255,255,255,0.05);
            backdrop-filter: blur(10px);
            border: 1px solid rgba(255,255,255,0.1);
            border-radius: 16px;
            padding: 2.5rem;
            width: 100%;
            max-width: 440px;
            box-shadow: 0 20px 60px rgba(0,0,0,0.3);
        }
        .header {
            text-align: center;
            margin-bottom: 2rem;
        }
        .shield-icon {
            width: 56px;
            height: 56px;
            margin: 0 auto 1rem;
            background: linear-gradient(135deg, #3b82f6, #6366f1);
            border-radius: 50%;
            display: flex;
            align-items: center;
            justify-content: center;
        }
        .shield-icon svg {
            width: 28px;
            height: 28px;
            fill: white;
        }
        .header h1 {
            font-size: 1.5rem;
            font-weight: 600;
            margin-bottom: 0.5rem;
        }
        .header p {
            color: #a1a1aa;
            font-size: 0.9rem;
        }
        .captcha-section {
            margin-bottom: 1.5rem;
        }
        .question-box {
            background: rgba(59, 130, 246, 0.1);
            border: 1px solid rgba(59, 130, 246, 0.3);
            border-radius: 10px;
            padding: 1.2rem;
            text-align: center;
            margin-bottom: 1rem;
        }
        .question-label {
            font-size: 0.8rem;
            color: #a1a1aa;
            margin-bottom: 0.5rem;
        }
        .question-text {
            font-size: 1.8rem;
            font-weight: 700;
            color: #60a5fa;
            font-family: 'Courier New', monospace;
            letter-spacing: 3px;
        }
        .input-group {
            margin-bottom: 1rem;
        }
        .input-group label {
            display: block;
            font-size: 0.85rem;
            color: #a1a1aa;
            margin-bottom: 0.4rem;
        }
        .input-group input {
            width: 100%;
            padding: 0.75rem 1rem;
            background: rgba(255,255,255,0.08);
            border: 1px solid rgba(255,255,255,0.15);
            border-radius: 8px;
            color: #e4e4e7;
            font-size: 1.1rem;
            text-align: center;
            letter-spacing: 2px;
            outline: none;
            transition: border-color 0.2s;
        }
        .input-group input:focus {
            border-color: #3b82f6;
        }
        /* 滑块验证 */
        .slider-section {
            margin-bottom: 1.5rem;
        }
        .slider-label {
            font-size: 0.85rem;
            color: #a1a1aa;
            margin-bottom: 0.6rem;
        }
        .slider-track {
            position: relative;
            height: 44px;
            background: rgba(255,255,255,0.08);
            border: 1px solid rgba(255,255,255,0.15);
            border-radius: 22px;
            overflow: hidden;
            user-select: none;
            cursor: pointer;
        }
        .slider-fill {
            position: absolute;
            left: 0;
            top: 0;
            height: 100%;
            background: linear-gradient(90deg, #3b82f6, #6366f1);
            opacity: 0.35;
            border-radius: 22px;
            transition: width 0.05s;
        }
        .slider-thumb {
            position: absolute;
            top: 5px;
            width: 34px;
            height: 34px;
            background: rgba(59, 130, 246, 0.5);
            border-radius: 50%;
            display: flex;
            align-items: center;
            justify-content: center;
            cursor: grab;
            box-shadow: 0 2px 8px rgba(0,0,0,0.2);
            z-index: 3;
        }
        .slider-thumb:active { cursor: grabbing; box-shadow: 0 2px 12px rgba(59,130,246,0.4); }
        .slider-thumb svg {
            width: 20px;
            height: 20px;
            fill: #ffffff;
        }
        .slider-text {
            position: absolute;
            top: 50%;
            left: 50%;
            transform: translate(-50%, -50%);
            color: #71717a;
            font-size: 0.85rem;
            pointer-events: none;
            z-index: 1;
        }
        .slider-target {
            position: absolute;
            top: 4px;
            width: 36px;
            height: 36px;
            background: transparent;
            border: 2px dashed rgba(99,102,241,0.6);
            border-radius: 50%;
            z-index: 2;
        }
        .btn-verify {
            width: 100%;
            padding: 0.85rem;
            background: linear-gradient(135deg, #3b82f6, #6366f1);
            color: white;
            border: none;
            border-radius: 10px;
            font-size: 1rem;
            font-weight: 500;
            cursor: pointer;
            transition: opacity 0.2s, transform 0.1s;
        }
        .btn-verify:hover { opacity: 0.9; }
        .btn-verify:active { transform: scale(0.98); }
        .btn-verify:disabled {
            opacity: 0.5;
            cursor: not-allowed;
        }
        .message {
            margin-top: 1rem;
            padding: 0.75rem;
            border-radius: 8px;
            font-size: 0.85rem;
            text-align: center;
            display: none;
        }
        .message.error {
            display: block;
            background: rgba(239, 68, 68, 0.1);
            border: 1px solid rgba(239, 68, 68, 0.3);
            color: #fca5a5;
        }
        .message.success {
            display: block;
            background: rgba(34, 197, 94, 0.1);
            border: 1px solid rgba(34, 197, 94, 0.3);
            color: #86efac;
        }
        .footer {
            text-align: center;
            margin-top: 1.5rem;
            color: #52525b;
            font-size: 0.8rem;
        }
        .loading { display: none; }
        .loading.show { display: block; text-align: center; color: #a1a1aa; }
        @keyframes spin {
            to { transform: rotate(360deg); }
        }
        .spinner {
            display: inline-block;
            width: 20px;
            height: 20px;
            border: 2px solid rgba(255,255,255,0.2);
            border-top-color: #3b82f6;
            border-radius: 50%;
            animation: spin 0.8s linear infinite;
            vertical-align: middle;
            margin-right: 0.5rem;
        }
    </style>
</head>
<body>
<div class="container">
    <div class="header">
        <div class="shield-icon">
            <svg viewBox="0 0 24 24"><path d="M12 1L3 5v6c0 5.55 3.84 10.74 9 12 5.16-1.26 9-6.45 9-12V5l-9-4zm0 10.99h7c-.53 4.12-3.28 7.79-7 8.94V12H5V6.3l7-3.11v8.8z"/></svg>
        </div>
        <h1>安全验证</h1>
        <p>检测到异常访问，请完成以下验证</p>
    </div>

    <div class="loading show" id="loadingArea">
        <span class="spinner"></span> 加载验证码...
    </div>

    <div id="captchaArea" style="display:none;">
        <!-- 算术验证码模式 -->
        <div class="captcha-section" id="mathSection" style="display:none;">
            <div class="question-box">
                <div class="question-label">请计算以下结果</div>
                <div class="question-text" id="questionText">--</div>
            </div>
            <div class="input-group">
                <label>输入答案</label>
                <input type="text" id="answerInput" placeholder="请输入计算结果" autocomplete="off" inputmode="numeric">
            </div>
        </div>

        <!-- 滑块验证模式 -->
        <div class="slider-section" id="sliderSection" style="display:none;">
            <div class="slider-label">拖动滑块到指定位置</div>
            <div class="slider-track" id="sliderTrack">
                <div class="slider-fill" id="sliderFill"></div>
                <div class="slider-target" id="sliderTarget"></div>
                <div class="slider-text" id="sliderHint">拖动滑块 →</div>
                <div class="slider-thumb" id="sliderThumb">
                    <svg viewBox="0 0 24 24"><path d="M8 5v14l11-7z"/></svg>
                </div>
            </div>
        </div>

        <button class="btn-verify" id="verifyBtn" onclick="submitVerify()">验证</button>
    </div>

    <div class="message" id="msgBox"></div>
    <div class="footer">Powered by LyWaf</div>
</div>

<script>
(function() {
    var token = '';
    var currentMode = '';
    var sliderValue = 0;
    var mouseTrack = [];
    var pageLoadTime = Date.now();
    var isDragging = false;

    // 记录鼠标活动
    document.addEventListener('mousemove', function(e) {
        mouseTrack.push({ x: e.clientX, y: e.clientY, t: Date.now() - pageLoadTime });
    });
    document.addEventListener('touchmove', function(e) {
        if (e.touches.length > 0) {
            mouseTrack.push({ x: e.touches[0].clientX, y: e.touches[0].clientY, t: Date.now() - pageLoadTime });
        }
    });

    // 加载验证码
    function loadChallenge() {
        document.getElementById('mathSection').style.display = 'none';
        document.getElementById('sliderSection').style.display = 'none';

        fetch('/__captcha/challenge')
            .then(function(r) { return r.json(); })
            .then(function(data) {
                if (data.success) {
                    token = data.token;
                    currentMode = data.mode;
                    document.getElementById('loadingArea').style.display = 'none';
                    document.getElementById('captchaArea').style.display = 'block';

                    if (currentMode === 'math') {
                        document.getElementById('questionText').textContent = data.question;
                        document.getElementById('mathSection').style.display = 'block';
                        document.getElementById('answerInput').value = '';
                        document.getElementById('answerInput').focus();
                    } else {
                        document.getElementById('sliderSection').style.display = 'block';
                        sliderValue = 0;
                        initSlider(data.sliderTarget);
                    }
                }
            })
            .catch(function() {
                document.getElementById('loadingArea').innerHTML = '加载失败，<a href="javascript:location.reload()" style="color:#60a5fa">点击重试</a>';
            });
    }

    // 初始化滑块
    function initSlider(serverTarget) {
        var track = document.getElementById('sliderTrack');
        var thumb = document.getElementById('sliderThumb');
        var fill = document.getElementById('sliderFill');
        var target = document.getElementById('sliderTarget');
        var hint = document.getElementById('sliderHint');
        var trackWidth = track.offsetWidth;

        // 目标位置由服务端下发
        var targetPos = (serverTarget / 100) * (trackWidth - 36) + 2;
        target.style.left = targetPos + 'px';

        // 重置滑块
        thumb.style.left = '0px';
        fill.style.width = '0px';
        hint.style.display = 'block';

        function updateSlider(pos) {
            var maxPos = trackWidth - 34;
            pos = Math.max(0, Math.min(pos, maxPos));
            thumb.style.left = pos + 'px';
            fill.style.width = (pos + 17) + 'px';
            sliderValue = Math.round((pos / maxPos) * 100);
            if (sliderValue > 5) hint.style.display = 'none';
            else hint.style.display = 'block';
        }

        // 鼠标事件
        thumb.onmousedown = function(e) {
            isDragging = true;
            var startX = e.clientX;
            var startLeft = parseInt(thumb.style.left) || 0;

            function onMove(e2) {
                if (!isDragging) return;
                updateSlider(startLeft + e2.clientX - startX);
            }
            function onUp() {
                isDragging = false;
                document.removeEventListener('mousemove', onMove);
                document.removeEventListener('mouseup', onUp);
            }
            document.addEventListener('mousemove', onMove);
            document.addEventListener('mouseup', onUp);
            e.preventDefault();
        };

        // 触摸事件
        thumb.ontouchstart = function(e) {
            isDragging = true;
            var startX = e.touches[0].clientX;
            var startLeft = parseInt(thumb.style.left) || 0;

            function onMove(e2) {
                if (!isDragging) return;
                updateSlider(startLeft + e2.touches[0].clientX - startX);
                e2.preventDefault();
            }
            function onEnd() {
                isDragging = false;
                document.removeEventListener('touchmove', onMove);
                document.removeEventListener('touchend', onEnd);
            }
            document.addEventListener('touchmove', onMove, { passive: false });
            document.addEventListener('touchend', onEnd);
            e.preventDefault();
        };
    }

    // 提交验证
    window.submitVerify = function() {
        var btn = document.getElementById('verifyBtn');
        var msgBox = document.getElementById('msgBox');
        var answer = '';

        if (currentMode === 'math') {
            answer = document.getElementById('answerInput').value;
            if (!answer) {
                showMsg('请输入验证码答案', 'error');
                return;
            }
        }

        btn.disabled = true;
        btn.textContent = '验证中...';
        msgBox.style.display = 'none';

        fetch('/__captcha/verify', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                token: token,
                answer: answer,
                behavior: {
                    mouseTrackCount: mouseTrack.length,
                    duration: Date.now() - pageLoadTime,
                    sliderOffset: sliderValue
                }
            })
        })
        .then(function(r) { return r.json(); })
        .then(function(data) {
            if (data.success) {
                showMsg('验证通过，正在刷新...', 'success');
                setTimeout(function() { location.reload(); }, 1000);
            } else {
                showMsg(data.message || '验证失败', 'error');
                btn.disabled = false;
                btn.textContent = '验证';
                // 刷新验证码（可能切换模式）
                mouseTrack = [];
                pageLoadTime = Date.now();
                loadChallenge();
            }
        })
        .catch(function() {
            showMsg('网络错误，请重试', 'error');
            btn.disabled = false;
            btn.textContent = '验证';
        });
    };

    function showMsg(text, type) {
        var el = document.getElementById('msgBox');
        el.textContent = text;
        el.className = 'message ' + type;
    }

    // 回车提交
    document.getElementById('answerInput').addEventListener('keydown', function(e) {
        if (e.key === 'Enter') window.submitVerify();
    });

    loadChallenge();
})();
</script>
</body>
</html>
""";
    }
}

/// <summary>
/// 验证码验证请求
/// </summary>
public class CaptchaVerifyRequest
{
    public string? Token { get; set; }
    public string? Answer { get; set; }
    public CaptchaBehaviorRequest? Behavior { get; set; }
}

/// <summary>
/// 行为验证数据请求
/// </summary>
public class CaptchaBehaviorRequest
{
    public int MouseTrackCount { get; set; }
    public long Duration { get; set; }
    public int SliderOffset { get; set; }
}
