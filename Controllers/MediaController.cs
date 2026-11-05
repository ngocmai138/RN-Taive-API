using Microsoft.AspNetCore.Mvc;
using RednoteDownloaderApi.Services;

namespace RednoteDownloaderApi.Controllers
{
    public class InspectRequest
    {
        public string Url { get; set; } = string.Empty;
    }

    public class ProcessRequest
    {
        public string VideoId { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty; // "download_original", "blur", "translate", "add_hook"
        public string? HookText { get; set; }
    }

    [ApiController]
    [Route("api/[controller]")]
    public class MediaController : ControllerBase
    {
        private readonly IMediaExtractorService _mediaExtractor;
        private readonly IVideoProcessingService _videoProcessor;
        private readonly IVideoCacheManager _cacheManager;
        private readonly IConfiguration _configuration;
        private readonly ILogger<MediaController> _logger;

        public MediaController(
            IMediaExtractorService mediaExtractor,
            IVideoProcessingService videoProcessor,
            IVideoCacheManager cacheManager,
            IConfiguration configuration,
            ILogger<MediaController> logger)
        {
            _mediaExtractor = mediaExtractor;
            _videoProcessor = videoProcessor;
            _cacheManager = cacheManager;
            _configuration = configuration;
            _logger = logger;
        }

        /// <summary>
        /// Bước 1: Tiếp nhận link Douyin/Rednote, tải video đệm và kiểm tra nhanh chữ tiếng Trung trong 1-2 giây
        /// </summary>
        [HttpPost("inspect")]
        public async Task<IActionResult> InspectMedia([FromBody] InspectRequest request, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(request?.Url))
            {
                return BadRequest(new { status = "error", message = "Vui lòng truyền URL Douyin hoặc Rednote" });
            }

            try
            {
                // 1. Trích xuất thông tin media
                var extractResult = await _mediaExtractor.ExtractMediaAsync(request.Url, cancellationToken);
                if (!extractResult.Success)
                {
                    return BadRequest(new { status = "error", message = extractResult.ErrorMessage });
                }

                if (extractResult.IsImageAlbum || string.IsNullOrEmpty(extractResult.VideoUrl))
                {
                    return BadRequest(new
                    {
                        status = "error",
                        message = "Bài viết này là album hình ảnh. Tính năng kiểm tra và dịch chỉ áp dụng cho Video."
                    });
                }

                // 2. Tải video gốc về thư mục tạm để phân tích
                string tempDir = _configuration["VideoProcessing:TempDir"] ?? "TempVideos";
                Directory.CreateDirectory(tempDir);

                string videoId = Guid.NewGuid().ToString("N");
                string originalFilePath = Path.Combine(tempDir, $"{videoId}_orig.mp4");

                await _mediaExtractor.DownloadVideoToTempAsync(extractResult.VideoUrl, extractResult.Platform, originalFilePath, cancellationToken);

                // 3. Quét nhanh chữ tiếng Trung (Quick Check)
                var checkResult = await _videoProcessor.CheckChineseAsync(originalFilePath, cancellationToken);

                // 4. Lưu metadata vào Cache Manager
                var cachedItem = new CachedVideoItem
                {
                    VideoId = videoId,
                    Title = extractResult.Title,
                    Platform = extractResult.Platform,
                    OriginalFilePath = originalFilePath,
                    HasChinese = checkResult.HasChinese,
                    DetectedSamples = checkResult.DetectedSamples ?? new List<string>()
                };
                _cacheManager.AddOrUpdate(cachedItem);

                string scheme = Request.Headers.TryGetValue("X-Forwarded-Proto", out var proto)
                    ? proto.ToString()
                    : (Request.Host.Host.Contains("localhost") ? Request.Scheme : "https");

                string downloadOriginalUrl = $"{scheme}://{Request.Host}/api/Media/download?id={videoId}";

                // Trường hợp 1: Không có chữ tiếng Trung
                if (!checkResult.HasChinese)
                {
                    List<string> suggestedHooks = new();
                    try
                    {
                        suggestedHooks = await _videoProcessor.GenerateHooksAsync(originalFilePath, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Không thể tự động sinh hook cho video: {VideoId}", videoId);
                    }

                    cachedItem.SuggestedHooks = suggestedHooks;
                    _cacheManager.AddOrUpdate(cachedItem);

                    return Ok(new
                    {
                        status = "success",
                        video_id = videoId,
                        title = extractResult.Title,
                        platform = extractResult.Platform,
                        has_chinese = false,
                        suggested_hooks = suggestedHooks,
                        message = "Video không chứa chữ tiếng Trung Quốc. Bạn có thể tải ngay video gốc hoặc chọn tiêu đề Hook viral để chèn vào video.",
                        options = new[]
                        {
                            new { action = "download_original", label = "Tải video gốc nguyên bản" },
                            new { action = "add_hook", label = "Chèn tiêu đề Hook tiếng Anh viral (phong cách Reels/TikTok)" }
                        },
                        download_url = downloadOriginalUrl
                    });
                }

                // Trường hợp 2: Có chữ tiếng Trung -> Trả về các lựa chọn cho người dùng
                return Ok(new
                {
                    status = "success",
                    video_id = videoId,
                    title = extractResult.Title,
                    platform = extractResult.Platform,
                    has_chinese = true,
                    sample_texts = checkResult.DetectedSamples,
                    message = "Phát hiện chữ tiếng Trung trong video. Vui lòng chọn hành động tiếp theo.",
                    options = new[]
                    {
                        new { action = "download_original", label = "Tải video gốc nguyên bản" },
                        new { action = "blur", label = "Làm mờ chữ tiếng Trung (không chèn chữ mới)" },
                        new { action = "translate", label = "Dịch sang tiếng Anh & chèn hộp trắng" }
                    },
                    download_original_url = downloadOriginalUrl
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi xảy ra trong quá trình kiểm tra video: {Url}", request.Url);
                return StatusCode(500, new { status = "error", message = ex.Message });
            }
        }

        /// <summary>
        /// Bước 2: Nhận lựa chọn của người dùng (blur, translate, hoặc download_original) và tiến hành xử lý
        /// </summary>
        [HttpPost("process")]
        public async Task<IActionResult> ProcessMedia([FromBody] ProcessRequest request, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(request?.VideoId))
            {
                return BadRequest(new { status = "error", message = "Vui lòng cung cấp VideoId" });
            }

            var item = _cacheManager.Get(request.VideoId);
            if (item == null || !System.IO.File.Exists(item.OriginalFilePath))
            {
                return NotFound(new { status = "error", message = "Video không tồn tại hoặc đã hết hạn tạm thời" });
            }

            string action = request.Action?.ToLower() ?? "download_original";

            string scheme = Request.Headers.TryGetValue("X-Forwarded-Proto", out var proto)
                ? proto.ToString()
                : (Request.Host.Host.Contains("localhost") ? Request.Scheme : "https");

            // Nếu người dùng chọn tải gốc
            if (action == "download_original")
            {
                return Ok(new
                {
                    status = "success",
                    action = "download_original",
                    download_url = $"{scheme}://{Request.Host}/api/Media/download?id={request.VideoId}"
                });
            }

            if (action != "blur" && action != "translate" && action != "add_hook")
            {
                return BadRequest(new { status = "error", message = "Hành động không hợp lệ. Chỉ chấp nhận 'download_original', 'blur', 'translate', hoặc 'add_hook'." });
            }

            try
            {
                string tempDir = _configuration["VideoProcessing:TempDir"] ?? "TempVideos";
                string outputPath = Path.Combine(tempDir, $"{request.VideoId}_{action}.mp4");

                var processResult = await _videoProcessor.ProcessVideoAsync(
                    item.OriginalFilePath,
                    outputPath,
                    action,
                    hookText: request.HookText,
                    cancellationToken: cancellationToken);
                if (processResult.Status != "success")
                {
                    return StatusCode(500, new { status = "error", message = processResult.Message ?? "Xử lý video thất bại" });
                }

                item.ProcessedFilePath = outputPath;
                _cacheManager.AddOrUpdate(item);

                return Ok(new
                {
                    status = "success",
                    action = action,
                    total_frames = processResult.TotalFrames,
                    download_url = $"{scheme}://{Request.Host}/api/Media/download?id={request.VideoId}&processed=true"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi xảy ra khi xử lý video: {VideoId}", request.VideoId);
                return StatusCode(500, new { status = "error", message = ex.Message });
            }
        }

        /// <summary>
        /// Gợi ý lại danh sách các tiêu đề Hook viral mới bằng AI
        /// </summary>
        [HttpPost("generate-hooks")]
        public async Task<IActionResult> GenerateHooks([FromBody] ProcessRequest request, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(request?.VideoId))
            {
                return BadRequest(new { status = "error", message = "Vui lòng cung cấp VideoId" });
            }

            var item = _cacheManager.Get(request.VideoId);
            if (item == null || !System.IO.File.Exists(item.OriginalFilePath))
            {
                return NotFound(new { status = "error", message = "Video không tồn tại hoặc đã hết hạn tạm thời" });
            }

            try
            {
                var hooks = await _videoProcessor.GenerateHooksAsync(item.OriginalFilePath, cancellationToken);
                item.SuggestedHooks = hooks;
                _cacheManager.AddOrUpdate(item);

                return Ok(new
                {
                    status = "success",
                    video_id = request.VideoId,
                    hooks = hooks
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi sinh hook: {VideoId}", request.VideoId);
                return StatusCode(500, new { status = "error", message = ex.Message });
            }
        }

        /// <summary>
        /// API 1 chạm dành riêng cho Phím tắt iPhone (iOS Shortcuts) hoặc ứng dụng tự động hóa:
        /// Tự động bóc tách -> xử lý video (auto/translate/blur/add_hook) -> trả về link video_url để phím tắt tải và lưu vào Album ảnh.
        /// </summary>
        [HttpGet("quick-process")]
        public async Task<IActionResult> QuickProcess(
            [FromQuery] string url,
            [FromQuery] string action = "auto",
            [FromQuery] string? hookText = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return BadRequest(new { status = "error", message = "Vui lòng truyền URL Douyin hoặc Rednote" });
            }

            try
            {
                // 1. Trích xuất thông tin media
                var extractResult = await _mediaExtractor.ExtractMediaAsync(url, cancellationToken);
                if (!extractResult.Success)
                {
                    return BadRequest(new { status = "error", message = extractResult.ErrorMessage });
                }

                if (extractResult.IsImageAlbum || string.IsNullOrEmpty(extractResult.VideoUrl))
                {
                    return BadRequest(new
                    {
                        status = "error",
                        message = "Bài viết này là album hình ảnh. Tính năng xử lý video chỉ áp dụng cho Video."
                    });
                }

                // 2. Tải video gốc về thư mục tạm
                string tempDir = _configuration["VideoProcessing:TempDir"] ?? "TempVideos";
                Directory.CreateDirectory(tempDir);

                string videoId = Guid.NewGuid().ToString("N");
                string originalFilePath = Path.Combine(tempDir, $"{videoId}_orig.mp4");

                await _mediaExtractor.DownloadVideoToTempAsync(extractResult.VideoUrl, extractResult.Platform, originalFilePath, cancellationToken);

                string targetAction = action.ToLower();
                string? finalHookText = hookText;

                // 3. Nếu là chế độ auto, tự động phát hiện ngôn ngữ để chọn hành động tối ưu
                if (targetAction == "auto")
                {
                    var checkResult = await _videoProcessor.CheckChineseAsync(originalFilePath, cancellationToken);
                    if (checkResult.HasChinese)
                    {
                        targetAction = "translate";
                    }
                    else
                    {
                        targetAction = "add_hook";
                    }
                }

                // Nếu cần add_hook nhưng chưa có hookText, tự sinh hook bằng AI Gemini
                if (targetAction == "add_hook" && string.IsNullOrWhiteSpace(finalHookText))
                {
                    try
                    {
                        var hooks = await _videoProcessor.GenerateHooksAsync(originalFilePath, cancellationToken);
                        if (hooks != null && hooks.Count > 0)
                        {
                            finalHookText = hooks[0];
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Không thể sinh hook tự động cho quick-process: {VideoId}", videoId);
                        finalHookText = "Wait until you see how this turns out";
                    }
                }

                string scheme = Request.Headers.TryGetValue("X-Forwarded-Proto", out var proto)
                    ? proto.ToString()
                    : (Request.Host.Host.Contains("localhost") ? Request.Scheme : "https");

                // Nếu chọn tải gốc
                if (targetAction == "download_original" || targetAction == "original")
                {
                    var origItem = new CachedVideoItem
                    {
                        VideoId = videoId,
                        Title = extractResult.Title,
                        Platform = extractResult.Platform,
                        OriginalFilePath = originalFilePath
                    };
                    _cacheManager.AddOrUpdate(origItem);

                    string directUrl = $"{scheme}://{Request.Host}/api/Media/download?id={videoId}";
                    return Ok(new
                    {
                        status = "success",
                        action = "download_original",
                        title = extractResult.Title,
                        video_url = directUrl,
                        download_url = directUrl
                    });
                }

                // 4. Tiến hành xử lý video (blur, translate, add_hook)
                string outputPath = Path.Combine(tempDir, $"{videoId}_{targetAction}.mp4");
                var processResult = await _videoProcessor.ProcessVideoAsync(
                    originalFilePath,
                    outputPath,
                    targetAction,
                    hookText: finalHookText,
                    cancellationToken: cancellationToken);

                if (processResult.Status != "success")
                {
                    return StatusCode(500, new { status = "error", message = processResult.Message ?? "Xử lý video thất bại" });
                }

                var cachedItem = new CachedVideoItem
                {
                    VideoId = videoId,
                    Title = extractResult.Title,
                    Platform = extractResult.Platform,
                    OriginalFilePath = originalFilePath,
                    ProcessedFilePath = outputPath
                };
                _cacheManager.AddOrUpdate(cachedItem);

                string downloadUrl = $"{scheme}://{Request.Host}/api/Media/download?id={videoId}&processed=true";

                return Ok(new
                {
                    status = "success",
                    action = targetAction,
                    title = extractResult.Title,
                    hook_used = finalHookText,
                    video_url = downloadUrl,
                    download_url = downloadUrl
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi xảy ra trong quick-process: {Url}", url);
                return StatusCode(500, new { status = "error", message = ex.Message });
            }
        }

        /// <summary>
        /// Bước 3: Tải video về máy người dùng và XÓA SẠCH NGAY LẬP TỨC toàn bộ file tạm sau khi truyền xong
        /// </summary>
        [HttpGet("download")]
        public IActionResult DownloadMedia([FromQuery] string id, [FromQuery] bool processed = false)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return BadRequest(new { status = "error", message = "Vui lòng cung cấp id video" });
            }

            var item = _cacheManager.Get(id);
            if (item == null)
            {
                return NotFound(new { status = "error", message = "Video không tồn tại hoặc đã được tải và dọn dẹp" });
            }

            string filePath = (processed && !string.IsNullOrEmpty(item.ProcessedFilePath))
                ? item.ProcessedFilePath
                : item.OriginalFilePath;

            if (!System.IO.File.Exists(filePath))
            {
                return NotFound(new { status = "error", message = "File video không còn tồn tại trên server" });
            }

            try
            {
                string safeTitle = SanitizeFileName(item.Title);
                string suffix = processed ? "_translated" : "";
                string downloadName = $"{safeTitle}{suffix}.mp4";

                var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);

                // QUAN TRỌNG: Đăng ký xóa sạch ngay lập tức mọi file tạm sau khi Response stream hoàn tất về máy người dùng
                HttpContext.Response.OnCompleted(() =>
                {
                    try
                    {
                        _cacheManager.DeleteVideoFiles(id);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Lỗi khi xóa file tạm sau khi tải: {VideoId}", id);
                    }
                    return Task.CompletedTask;
                });

                return File(fileStream, "video/mp4", downloadName, enableRangeProcessing: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi stream tải file video: {VideoId}", id);
                return StatusCode(500, new { status = "error", message = ex.Message });
            }
        }

        private static string SanitizeFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return "video";
            var invalidChars = Path.GetInvalidFileNameChars();
            var clean = new string(fileName.Where(ch => !invalidChars.Contains(ch) && ch != '\n' && ch != '\r').ToArray());
            return clean.Length > 50 ? clean.Substring(0, 50).Trim() : clean.Trim();
        }
    }
}

