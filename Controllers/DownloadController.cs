using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RednoteDownloaderApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class DownloadController : ControllerBase
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<DownloadController> _logger;

        public DownloadController(IHttpClientFactory httpClientFactory, ILogger<DownloadController> logger)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        /// <summary>
        /// Bóc tách link video hoặc bộ ảnh từ bài viết Rednote / Xiaohongshu
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetMedia([FromQuery] string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return BadRequest(new { status = "error", message = "Vui lòng truyền tham số url" });
            }

            try
            {
                // 1. Trích xuất link sạch kể cả khi người dùng dán kèm tiêu đề tiếng Trung
                var urlMatch = Regex.Match(url, @"https?://[^\s\u4e00-\u9fa5""<>]+");
                if (!urlMatch.Success)
                {
                    return BadRequest(new { status = "error", message = "Không tìm thấy URL hợp lệ trong chuỗi nhập vào" });
                }

                string targetUrl = urlMatch.Value.TrimEnd('.', ',', '，', '。', ';', '；', '!', '！');

                // 2. Chống SSRF: Chỉ cho phép domain của Rednote / Xiaohongshu
                if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var parsedUri) ||
                    (!parsedUri.Host.EndsWith("xiaohongshu.com", StringComparison.OrdinalIgnoreCase) &&
                     !parsedUri.Host.EndsWith("xhslink.com", StringComparison.OrdinalIgnoreCase)))
                {
                    return BadRequest(new
                    {
                        status = "error",
                        message = "Chỉ hỗ trợ đường dẫn từ Xiaohongshu / Rednote (xhslink.com hoặc xiaohongshu.com)"
                    });
                }

                // 3. Nếu là link rút gọn xhslink.com, giải quyết redirect Location trước
                if (targetUrl.Contains("xhslink.com", StringComparison.OrdinalIgnoreCase))
                {
                    using var redirectHandler = new HttpClientHandler { AllowAutoRedirect = false };
                    using var redirectClient = new HttpClient(redirectHandler) { Timeout = TimeSpan.FromSeconds(10) };
                    redirectClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36");

                    var headResp = await redirectClient.GetAsync(targetUrl);
                    var location = headResp.Headers.Location?.ToString();
                    if (!string.IsNullOrEmpty(location))
                    {
                        targetUrl = location.StartsWith("/") ? $"https://www.xiaohongshu.com{location}" : location;
                    }
                }

                // 4. Chuyển đổi định dạng /discovery/item/{noteId} sang /explore/{noteId} để lấy dữ liệu SSR đầy đủ nhất
                var discoveryMatch = Regex.Match(targetUrl, @"/discovery/item/([a-zA-Z0-9]+)");
                if (discoveryMatch.Success)
                {
                    string noteId = discoveryMatch.Groups[1].Value;
                    var tokenMatch = Regex.Match(targetUrl, @"xsec_token=([^&]+)");
                    string tokenParam = tokenMatch.Success ? $"?xsec_token={tokenMatch.Groups[1].Value}" : "";
                    targetUrl = $"https://www.xiaohongshu.com/explore/{noteId}{tokenParam}";
                }

                var client = _httpClientFactory.CreateClient("RednoteClient");
                var response = await client.GetAsync(targetUrl);

                if (!response.IsSuccessStatusCode)
                {
                    return StatusCode((int)response.StatusCode, new
                    {
                        status = "error",
                        message = $"Không thể truy cập đường dẫn Rednote (Mã lỗi: {response.StatusCode})"
                    });
                }

                // Đọc raw bytes và decode UTF-8 để tránh lỗi 'The character set provided in ContentType is invalid'
                var rawBytes = await response.Content.ReadAsByteArrayAsync();
                var htmlContent = System.Text.Encoding.UTF8.GetString(rawBytes);

                // 5. Phân tích dữ liệu JSON trạng thái gốc từ SSR (window.__INITIAL_STATE__)
                var stateMatch = Regex.Match(htmlContent, @"window\.__INITIAL_STATE__\s*=\s*(\{.+?\})\s*</script>", RegexOptions.Singleline);
                if (stateMatch.Success)
                {
                    try
                    {
                        string rawJson = stateMatch.Groups[1].Value.Replace("undefined", "null");
                        using var doc = JsonDocument.Parse(rawJson);

                        if (doc.RootElement.TryGetProperty("note", out var noteProp) &&
                            noteProp.TryGetProperty("noteDetailMap", out var mapProp))
                        {
                            foreach (var item in mapProp.EnumerateObject())
                            {
                                if (item.Value.TryGetProperty("note", out var noteData))
                                {
                                    string title = noteData.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                                    string desc = noteData.TryGetProperty("desc", out var d) ? d.GetString() ?? "" : "";
                                    string displayTitle = !string.IsNullOrWhiteSpace(title) ? title : desc;

                                    string author = "";
                                    string avatar = "";
                                    if (noteData.TryGetProperty("user", out var userProp))
                                    {
                                        author = userProp.TryGetProperty("nickname", out var nick) ? nick.GetString() ?? "" : "";
                                        avatar = userProp.TryGetProperty("avatar", out var avt) ? avt.GetString() ?? "" : "";
                                    }

                                    // Lấy ảnh bìa
                                    string coverUrl = "";
                                    if (noteData.TryGetProperty("imageList", out var imgCoverList) && imgCoverList.GetArrayLength() > 0)
                                    {
                                        var firstImg = imgCoverList[0];
                                        coverUrl = firstImg.TryGetProperty("urlDefault", out var uDef) ? uDef.GetString() ?? "" : "";
                                    }

                                    // Trường hợp 1: Bài viết có chứa Video
                                    if (noteData.TryGetProperty("video", out var videoProp))
                                    {
                                        string? videoUrl = null;

                                        // Ưu tiên 1: Link video gốc không logo từ originVideoKey
                                        if (videoProp.TryGetProperty("consumer", out var consumer) &&
                                            consumer.TryGetProperty("originVideoKey", out var originKey) &&
                                            !string.IsNullOrEmpty(originKey.GetString()))
                                        {
                                            videoUrl = $"https://sns-video-bd.xhscdn.com/{originKey.GetString()}";
                                        }

                                        // Ưu tiên 2: Link stream h264 độ phân giải tốt nhất từ media.stream
                                        if (string.IsNullOrEmpty(videoUrl) &&
                                            videoProp.TryGetProperty("media", out var media) &&
                                            media.TryGetProperty("stream", out var stream) &&
                                            stream.TryGetProperty("h264", out var h264List) &&
                                            h264List.GetArrayLength() > 0)
                                        {
                                            videoUrl = h264List[0].GetProperty("masterUrl").GetString();
                                        }

                                        // Ưu tiên 3: Tìm trong mediaV2 nếu chuỗi JSON lồng nhau
                                        if (string.IsNullOrEmpty(videoUrl) && videoProp.TryGetProperty("mediaV2", out var mediaV2Prop))
                                        {
                                            string mediaV2Str = mediaV2Prop.GetString() ?? "";
                                            var v2MasterMatch = Regex.Match(mediaV2Str, @"""master_url"":\s*""([^""]+)""");
                                            if (v2MasterMatch.Success)
                                            {
                                                videoUrl = v2MasterMatch.Groups[1].Value.Replace(@"\/", "/");
                                            }
                                        }

                                        if (!string.IsNullOrEmpty(videoUrl))
                                        {
                                            string proxyDownloadUrl = $"{Request.Scheme}://{Request.Host}/api/download/proxy?fileUrl={Uri.EscapeDataString(videoUrl)}&fileName={Uri.EscapeDataString(SanitizeFileName(displayTitle))}.mp4";

                                            return Ok(new
                                            {
                                                status = "success",
                                                type = "video",
                                                title = displayTitle,
                                                author,
                                                avatar,
                                                cover_url = coverUrl,
                                                video_url = videoUrl,
                                                download_url = proxyDownloadUrl
                                            });
                                        }
                                    }

                                    // Trường hợp 2: Bài viết dạng Album ảnh
                                    if (noteData.TryGetProperty("imageList", out var imgList) && imgList.GetArrayLength() > 0)
                                    {
                                        var images = new List<object>();
                                        int index = 1;
                                        foreach (var img in imgList.EnumerateArray())
                                        {
                                            string imgUrl = img.TryGetProperty("urlDefault", out var u) ? u.GetString() ?? "" : "";
                                            if (!string.IsNullOrEmpty(imgUrl))
                                            {
                                                images.Add(new
                                                {
                                                    url = imgUrl,
                                                    download_url = $"{Request.Scheme}://{Request.Host}/api/download/proxy?fileUrl={Uri.EscapeDataString(imgUrl)}&fileName={Uri.EscapeDataString(SanitizeFileName(displayTitle))}_{index}.jpg"
                                                });
                                                index++;
                                            }
                                        }

                                        return Ok(new
                                        {
                                            status = "success",
                                            type = "images",
                                            title = displayTitle,
                                            author,
                                            avatar,
                                            images
                                        });
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Phân tích JSON __INITIAL_STATE__ không thành công, chuyển sang Regex fallback.");
                    }
                }

                // 4. Fallback bằng Regex quét toàn bộ nội dung HTML
                var match = Regex.Match(htmlContent, @"https?:\\?/\\?/sns-video[^""'\s\\]+");
                if (!match.Success)
                {
                    match = Regex.Match(htmlContent, @"https?:\\?/\\?/[^""'\s\\]+\.mp4[^""'\s]*");
                }

                if (!match.Success)
                {
                    return NotFound(new
                    {
                        status = "error",
                        message = "Không tìm thấy nội dung video hoặc album ảnh hợp lệ trong bài viết Rednote này."
                    });
                }

                string rawUrl = match.Value;
                string cleanVideoUrl = rawUrl.Replace(@"\/", "/").Replace(@"\", "");

                return Ok(new
                {
                    status = "success",
                    type = "video",
                    title = "Rednote Video",
                    video_url = cleanVideoUrl,
                    download_url = $"{Request.Scheme}://{Request.Host}/api/download/proxy?fileUrl={Uri.EscapeDataString(cleanVideoUrl)}&fileName=rednote_video.mp4"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi xảy ra trong quá trình xử lý bài viết Rednote.");
                return StatusCode(500, new
                {
                    status = "error",
                    message = ex.Message
                });
            }
        }

        /// <summary>
        /// Endpoint trung gian (Proxy) tải trực tiếp file về máy người dùng,
        /// tự động gắn Referer của Xiaohongshu để tránh mã lỗi 403 Forbidden từ CDN.
        /// </summary>
        [HttpGet("proxy")]
        public async Task<IActionResult> ProxyDownload([FromQuery] string fileUrl, [FromQuery] string? fileName)
        {
            if (string.IsNullOrWhiteSpace(fileUrl))
            {
                return BadRequest(new { status = "error", message = "Vui lòng truyền tham số fileUrl" });
            }

            // Chống SSRF: Chỉ cho phép proxy từ CDN của Xiaohongshu / Rednote
            if (!Uri.TryCreate(fileUrl, UriKind.Absolute, out var parsedUri) ||
                (!parsedUri.Host.EndsWith("xhscdn.com", StringComparison.OrdinalIgnoreCase) &&
                 !parsedUri.Host.EndsWith("xiaohongshu.com", StringComparison.OrdinalIgnoreCase)))
            {
                return BadRequest(new { status = "error", message = "Domain file không hợp lệ hoặc không thuộc CDN Xiaohongshu" });
            }

            try
            {
                var client = _httpClientFactory.CreateClient("CdnProxyClient");
                var response = await client.GetAsync(fileUrl, HttpCompletionOption.ResponseHeadersRead);

                if (!response.IsSuccessStatusCode)
                {
                    return StatusCode((int)response.StatusCode, new
                    {
                        status = "error",
                        message = $"Không thể tải file từ CDN (Mã lỗi: {response.StatusCode})"
                    });
                }

                var stream = await response.Content.ReadAsStreamAsync();
                string contentType = response.Content.Headers.ContentType?.MediaType ?? (fileUrl.Contains(".jpg") ? "image/jpeg" : "video/mp4");
                string downloadName = !string.IsNullOrWhiteSpace(fileName) ? fileName : $"rednote_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.mp4";

                // Hỗ trợ Range Processing để trình duyệt có thể tua video mượt mà
                return File(stream, contentType, downloadName, enableRangeProcessing: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi xảy ra khi proxy tải file từ CDN.");
                return StatusCode(500, new { status = "error", message = ex.Message });
            }
        }

        private static string SanitizeFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return "rednote_media";
            var invalidChars = Path.GetInvalidFileNameChars();
            var clean = new string(fileName.Where(ch => !invalidChars.Contains(ch) && ch != '\n' && ch != '\r').ToArray());
            return clean.Length > 50 ? clean.Substring(0, 50).Trim() : clean.Trim();
        }
    }
}