using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;

namespace RednoteDownloaderApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    // [Route("api/DownloadDouyin")]  // Removed duplicate route to avoid routing conflicts
    public class DouyinController : ControllerBase
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<DouyinController> _logger;

        private const string MobileUserAgent = "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1";

        private static string _cachedTtwid = "";
        private static readonly object _ttwidLock = new();

        public DouyinController(IHttpClientFactory httpClientFactory, ILogger<DouyinController> logger)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        /// <summary>
        /// Bóc tách thông tin video / album ảnh Douyin không logo (watermark) từ đường link hoặc văn bản chia sẻ
        /// </summary>
        [HttpGet]
        [HttpHead]
        public async Task<IActionResult> GetMedia([FromQuery] string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return BadRequest(new { status = "error", message = "Vui lòng truyền tham số url" });
            }

            try
            {
                var mediaInfo = await ExtractDouyinMediaAsync(url);
                if (mediaInfo == null)
                {
                    return NotFound(new { status = "error", message = "Không tìm thấy nội dung video hoặc hình ảnh từ link Douyin này" });
                }

                string scheme = Request.Headers.TryGetValue("X-Forwarded-Proto", out var proto)
                    ? proto.ToString()
                    : (Request.Host.Host.Contains("localhost") ? Request.Scheme : "https");

                string safeTitle = SanitizeFileName(mediaInfo.Title);

                // Trường hợp 1: Bài viết dạng Album ảnh
                if (mediaInfo.IsImageAlbum)
                {
                    var images = new List<object>();
                    int index = 1;
                    foreach (var imgUrl in mediaInfo.Images)
                    {
                        images.Add(new
                        {
                            url = imgUrl,
                            download_url = $"{scheme}://{Request.Host}/api/Douyin/proxy?fileUrl={Uri.EscapeDataString(imgUrl)}&fileName={Uri.EscapeDataString(safeTitle)}_{index}.jpg"
                        });
                        index++;
                    }

                    return Ok(new
                    {
                        status = "success",
                        type = "images",
                        title = mediaInfo.Title,
                        author = mediaInfo.Author,
                        avatar = mediaInfo.Avatar,
                        images
                    });
                }

                // Trường hợp 2: Bài viết dạng Video
                string proxyDownloadUrl = $"{scheme}://{Request.Host}/api/Douyin/proxy?fileUrl={Uri.EscapeDataString(mediaInfo.VideoUrl)}&fileName={Uri.EscapeDataString(safeTitle)}.mp4";

                return Ok(new
                {
                    status = "success",
                    type = "video",
                    title = mediaInfo.Title,
                    author = mediaInfo.Author,
                    avatar = mediaInfo.Avatar,
                    cover_url = mediaInfo.CoverUrl,
                    video_url = mediaInfo.VideoUrl,
                    download_url = proxyDownloadUrl
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi xảy ra trong quá trình xử lý link Douyin: {Url}", url);
                return StatusCode(500, new { status = "error", message = ex.Message });
            }
        }

        /// <summary>
        /// API tải trực tiếp file video MP4 không logo về máy người dùng
        /// </summary>
        [HttpGet("download")]
        [HttpHead("download")]
        public async Task<IActionResult> DownloadVideo([FromQuery] string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return BadRequest(new { status = "error", message = "Vui lòng truyền tham số url" });
            }

            try
            {
                var mediaInfo = await ExtractDouyinMediaAsync(url);
                if (mediaInfo == null || string.IsNullOrEmpty(mediaInfo.VideoUrl))
                {
                    return NotFound(new { status = "error", message = "Không tìm thấy link video để tải" });
                }

                var cdnClient = _httpClientFactory.CreateClient("DouyinCdnClient");
                var response = await cdnClient.GetAsync(mediaInfo.VideoUrl, HttpCompletionOption.ResponseHeadersRead);

                if (!response.IsSuccessStatusCode)
                {
                    return StatusCode((int)response.StatusCode, new { status = "error", message = $"Không thể tải video từ CDN Douyin (Mã lỗi: {response.StatusCode})" });
                }

                // Return video info as JSON instead of binary stream
                return Ok(new
                {
                    status = "success",
                    platform = "douyin",
                    title = mediaInfo.Title,
                    video_url = mediaInfo.VideoUrl
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi xảy ra khi tải video Douyin: {Url}", url);
                return StatusCode(500, new { status = "error", message = ex.Message });
            }
        }

        /// <summary>
        /// Proxy trung gian tải dữ liệu video/ảnh từ CDN Douyin / ByteDance để tránh lỗi CORS hoặc chặn Referer
        /// </summary>
        [HttpGet("proxy")]
        [HttpHead("proxy")]
        public async Task<IActionResult> ProxyDownload([FromQuery] string fileUrl, [FromQuery] string? fileName)
        {
            if (string.IsNullOrWhiteSpace(fileUrl))
            {
                return BadRequest(new { status = "error", message = "Vui lòng truyền tham số fileUrl" });
            }

            // Chống SSRF: Chỉ cho phép proxy từ các domain CDN chính thức của Douyin / ByteDance
            if (!Uri.TryCreate(fileUrl, UriKind.Absolute, out var parsedUri) ||
                (!parsedUri.Host.EndsWith("douyinvod.com", StringComparison.OrdinalIgnoreCase) &&
                 !parsedUri.Host.EndsWith("snssdk.com", StringComparison.OrdinalIgnoreCase) &&
                 !parsedUri.Host.EndsWith("douyinpic.com", StringComparison.OrdinalIgnoreCase) &&
                 !parsedUri.Host.EndsWith("bytecdn.cn", StringComparison.OrdinalIgnoreCase) &&
                 !parsedUri.Host.EndsWith("ibytedtos.com", StringComparison.OrdinalIgnoreCase) &&
                 !parsedUri.Host.EndsWith("amemv.com", StringComparison.OrdinalIgnoreCase)))
            {
                return BadRequest(new { status = "error", message = "Domain file không thuộc máy chủ CDN của Douyin" });
            }

            try
            {
                var cdnClient = _httpClientFactory.CreateClient("DouyinCdnClient");
                var response = await cdnClient.GetAsync(fileUrl, HttpCompletionOption.ResponseHeadersRead);

                if (!response.IsSuccessStatusCode)
                {
                    return StatusCode((int)response.StatusCode, new { status = "error", message = $"Không thể tải file từ CDN (Mã lỗi: {response.StatusCode})" });
                }

                var stream = await response.Content.ReadAsStreamAsync();
                string contentType = response.Content.Headers.ContentType?.MediaType ?? (fileUrl.Contains(".jpg") || fileUrl.Contains(".jpeg") ? "image/jpeg" : "video/mp4");
                string downloadName = !string.IsNullOrWhiteSpace(fileName) ? fileName : $"douyin_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.mp4";

                return File(stream, contentType, downloadName, enableRangeProcessing: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi xảy ra khi proxy tải file từ CDN Douyin");
                return StatusCode(500, new { status = "error", message = ex.Message });
            }
        }

        #region Core Extraction Logic

        private async Task<DouyinMediaInfo?> ExtractDouyinMediaAsync(string input)
        {
            var douyinClient = _httpClientFactory.CreateClient("DouyinClient");

            // 1. Trích xuất ID bài viết từ URL hoặc link rút gọn v.douyin.com
            string videoId = "";
            var directIdMatch = Regex.Match(input, @"(?:video/|modal_id=)(\d+)");
            if (directIdMatch.Success)
            {
                videoId = directIdMatch.Groups[1].Value;
            }
            else
            {
                var shortLinkMatch = Regex.Match(input, @"https?://v\.douyin\.com/[a-zA-Z0-9_\-]+/?");
                if (!shortLinkMatch.Success)
                {
                    return null;
                }

                string shortUrl = shortLinkMatch.Value;
                using var reqRedirect = new HttpRequestMessage(HttpMethod.Get, shortUrl);
                using var redirectRes = await douyinClient.SendAsync(reqRedirect);

                string destinationUrl = redirectRes.Headers.Location?.ToString() ?? "";
                if (string.IsNullOrEmpty(destinationUrl))
                {
                    destinationUrl = redirectRes.RequestMessage?.RequestUri?.ToString() ?? "";
                }

                var idMatch = Regex.Match(destinationUrl, @"(?:video/|modal_id=)(\d+)");
                if (idMatch.Success)
                {
                    videoId = idMatch.Groups[1].Value;
                }
            }

            if (string.IsNullOrEmpty(videoId))
            {
                _logger.LogWarning("Không trích xuất được Video ID từ input: {Input}", input);
                return null;
            }

            // 2. Lấy cookie bảo mật ttwid (dùng cache nếu còn hiệu lực để tăng tốc, hoặc lấy mới)
            string ttwidCookie = _cachedTtwid;
            if (string.IsNullOrEmpty(ttwidCookie))
            {
                ttwidCookie = await GetTtwidCookieAsync(douyinClient, videoId);
                if (!string.IsNullOrEmpty(ttwidCookie))
                {
                    _cachedTtwid = ttwidCookie;
                }
            }

            // 3. Tải nội dung trang chi tiết iesdouyin.com với cookie ttwid
            string detailUrl = $"https://www.iesdouyin.com/share/video/{videoId}/";
            string html = await FetchDetailHtmlAsync(douyinClient, detailUrl, ttwidCookie);

            // Nếu HTML chưa chứa videoInfoRes (có thể do cookie ttwid cũ hết hạn), làm mới cookie và thử lại 1 lần
            if (!html.Contains("videoInfoRes"))
            {
                _logger.LogInformation("HTML chưa có videoInfoRes, thử làm mới cookie ttwid...");
                string freshTtwid = await GetTtwidCookieAsync(douyinClient, videoId);
                if (!string.IsNullOrEmpty(freshTtwid) && freshTtwid != ttwidCookie)
                {
                    _cachedTtwid = freshTtwid;
                    html = await FetchDetailHtmlAsync(douyinClient, detailUrl, freshTtwid);
                }
            }

            _logger.LogInformation("Video ID: {VideoId}, ttwid: {TtwidLength} ký tự, HTML: {HtmlLength} bytes, Có videoInfoRes: {HasInfo}", 
                videoId, _cachedTtwid.Length, html.Length, html.Contains("videoInfoRes"));

            var result = new DouyinMediaInfo { VideoId = videoId };

            // 4. Bóc tách dữ liệu JSON từ window._ROUTER_DATA (chứa loaderData và videoInfoRes)
            var routerMatch = Regex.Match(html, @"window\._ROUTER_DATA\s*=\s*(\{.+?\});?\s*</script>", RegexOptions.Singleline);
            if (!routerMatch.Success)
            {
                routerMatch = Regex.Match(html, @"window\._SSR_DATA\s*=\s*(\{.+?\});?\s*</script>", RegexOptions.Singleline);
            }

            if (routerMatch.Success)
            {
                try
                {
                    using var doc = JsonDocument.Parse(routerMatch.Groups[1].Value);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("loaderData", out var loaderData))
                    {
                        foreach (var prop in loaderData.EnumerateObject())
                        {
                            // Kiểm tra prop.Value phải là Object (tránh lỗi null ở video_layout)
                            if (prop.Value.ValueKind == JsonValueKind.Object &&
                                prop.Value.TryGetProperty("videoInfoRes", out var infoRes) &&
                                infoRes.TryGetProperty("item_list", out var itemList) &&
                                itemList.GetArrayLength() > 0)
                            {
                                var item = itemList[0];

                                if (item.TryGetProperty("desc", out var descProp))
                                {
                                    result.Title = descProp.GetString() ?? "";
                                }

                                if (item.TryGetProperty("author", out var authorProp))
                                {
                                    if (authorProp.TryGetProperty("nickname", out var nickProp))
                                    {
                                        result.Author = nickProp.GetString() ?? "";
                                    }
                                    if (authorProp.TryGetProperty("avatar_thumb", out var avtThumb) &&
                                        avtThumb.TryGetProperty("url_list", out var avtList) &&
                                        avtList.GetArrayLength() > 0)
                                    {
                                        result.Avatar = avtList[0].GetString() ?? "";
                                    }
                                }

                                // Trường hợp Album ảnh
                                if (item.TryGetProperty("images", out var imagesProp) &&
                                    imagesProp.ValueKind == JsonValueKind.Array &&
                                    imagesProp.GetArrayLength() > 0)
                                {
                                    foreach (var img in imagesProp.EnumerateArray())
                                    {
                                        if (img.TryGetProperty("url_list", out var uList) && uList.GetArrayLength() > 0)
                                        {
                                            result.Images.Add(uList[0].GetString() ?? "");
                                        }
                                    }
                                }

                                // Trường hợp Video
                                if (item.TryGetProperty("video", out var vProp))
                                {
                                    if (vProp.TryGetProperty("cover", out var coverProp) &&
                                        coverProp.TryGetProperty("url_list", out var coverList) &&
                                        coverList.GetArrayLength() > 0)
                                    {
                                        result.CoverUrl = coverList[0].GetString() ?? "";
                                    }

                                    string playUri = "";
                                    string playWmUrl = "";

                                    if (vProp.TryGetProperty("play_addr", out var pAddr))
                                    {
                                        if (pAddr.TryGetProperty("uri", out var uriProp))
                                        {
                                            playUri = uriProp.GetString() ?? "";
                                        }
                                        if (pAddr.TryGetProperty("url_list", out var pList) && pList.GetArrayLength() > 0)
                                        {
                                            playWmUrl = pList[0].GetString() ?? "";
                                        }
                                    }

                                    // Tạo link play video gốc không logo (playwm -> play)
                                    string cleanPlayUrl = !string.IsNullOrEmpty(playUri)
                                        ? $"https://aweme.snssdk.com/aweme/v1/play/?video_id={playUri}&ratio=1080p&line=0"
                                        : playWmUrl.Replace("playwm", "play").Replace("ratio=720p", "ratio=1080p");

                                    // 5. Giải mã 302 redirect từ snssdk sang link file MP4 CDN trực tiếp (douyinvod)
                                    string directCdnUrl = await ResolveDirectCdnUrlAsync(cleanPlayUrl);
                                    result.VideoUrl = !string.IsNullOrEmpty(directCdnUrl) ? directCdnUrl : cleanPlayUrl;
                                }

                                break;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Lỗi phân tích JSON _ROUTER_DATA của Douyin");
                }
            }

            // Fallback Regex nếu không bóc tách được từ _ROUTER_DATA
            if (string.IsNullOrEmpty(result.VideoUrl) && result.Images.Count == 0)
            {
                var cdnMatch = Regex.Match(html, @"https?://[^""'\s\\]+?(?:douyinvod|snssdk|douyinpic)[^""'\s\\]*?\.mp4[^""'\s]*");
                if (cdnMatch.Success)
                {
                    result.VideoUrl = cdnMatch.Value.Replace(@"\/", "/").Replace(@"\", "");
                }
            }

            // Fallback tiêu đề từ thẻ <title>
            if (string.IsNullOrEmpty(result.Title))
            {
                var titleMatch = Regex.Match(html, @"<title>(.*?)</title>");
                if (titleMatch.Success)
                {
                    result.Title = HttpUtility.HtmlDecode(titleMatch.Groups[1].Value.Trim());
                }
            }

            if (string.IsNullOrEmpty(result.VideoUrl) && result.Images.Count == 0)
            {
                return null;
            }

            return result;
        }

        private static async Task<string> FetchDetailHtmlAsync(HttpClient client, string detailUrl, string ttwid)
        {
            using var detailReq = new HttpRequestMessage(HttpMethod.Get, detailUrl);
            detailReq.Headers.Add("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
            if (!string.IsNullOrEmpty(ttwid))
            {
                detailReq.Headers.Add("Cookie", $"ttwid={ttwid}");
            }
            using var detailRes = await client.SendAsync(detailReq);
            return await detailRes.Content.ReadAsStringAsync();
        }

        private async Task<string> GetTtwidCookieAsync(HttpClient client, string videoId)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"https://m.douyin.com/share/video/{videoId}");
                using var response = await client.SendAsync(request);

                if (response.Headers.TryGetValues("Set-Cookie", out var cookieHeaders))
                {
                    foreach (var header in cookieHeaders)
                    {
                        var match = Regex.Match(header, @"ttwid=([^;]+)");
                        if (match.Success)
                        {
                            return match.Groups[1].Value;
                        }
                    }
                }
                else
                {
                    _logger.LogWarning("Không thấy header Set-Cookie trong response từ m.douyin.com");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Không thể lấy cookie ttwid từ m.douyin.com");
            }

            return string.Empty;
        }

        private async Task<string> ResolveDirectCdnUrlAsync(string playUrl)
        {
            if (string.IsNullOrEmpty(playUrl)) return string.Empty;

            try
            {
                var douyinClient = _httpClientFactory.CreateClient("DouyinClient");
                using var req = new HttpRequestMessage(HttpMethod.Get, playUrl);
                using var res = await douyinClient.SendAsync(req);

                var location = res.Headers.Location?.ToString();
                if (!string.IsNullOrEmpty(location))
                {
                    return location;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Không thể giải mã link chuyển hướng CDN từ {PlayUrl}", playUrl);
            }

            return playUrl;
        }

        private static string SanitizeFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return "douyin_video";
            var invalidChars = Path.GetInvalidFileNameChars();
            var clean = new string(fileName.Where(ch => !invalidChars.Contains(ch) && ch != '\n' && ch != '\r').ToArray());
            return clean.Length > 50 ? clean.Substring(0, 50).Trim() : clean.Trim();
        }

        #endregion
    }

    public class DouyinMediaInfo
    {
        public string VideoId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Author { get; set; } = string.Empty;
        public string Avatar { get; set; } = string.Empty;
        public string CoverUrl { get; set; } = string.Empty;
        public string VideoUrl { get; set; } = string.Empty;
        public List<string> Images { get; set; } = new();
        public bool IsImageAlbum => Images.Count > 0;
    }
}