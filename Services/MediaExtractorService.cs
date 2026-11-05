using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;

namespace RednoteDownloaderApi.Services
{
    public class MediaExtractorService : IMediaExtractorService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<MediaExtractorService> _logger;

        private static string _cachedTtwid = "";
        private static readonly object _ttwidLock = new();

        public MediaExtractorService(IHttpClientFactory httpClientFactory, ILogger<MediaExtractorService> logger)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        public async Task<MediaExtractResult> ExtractMediaAsync(string inputUrl, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(inputUrl))
            {
                return new MediaExtractResult { Success = false, ErrorMessage = "URL không được để trống" };
            }

            // Kiểm tra xem là link Douyin hay Rednote
            if (inputUrl.Contains("douyin.com", StringComparison.OrdinalIgnoreCase))
            {
                return await ExtractDouyinAsync(inputUrl, cancellationToken);
            }
            else if (inputUrl.Contains("xiaohongshu.com", StringComparison.OrdinalIgnoreCase) || inputUrl.Contains("xhslink.com", StringComparison.OrdinalIgnoreCase))
            {
                return await ExtractRednoteAsync(inputUrl, cancellationToken);
            }

            return new MediaExtractResult
            {
                Success = false,
                ErrorMessage = "Đường dẫn không được hỗ trợ. Vui lòng cung cấp link từ Douyin hoặc Rednote (Xiaohongshu)."
            };
        }

        public async Task<string> DownloadVideoToTempAsync(string videoUrl, string platform, string destinationPath, CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

            string clientName = platform.ToLower() == "douyin" ? "DouyinCdnClient" : "CdnProxyClient";
            var client = _httpClientFactory.CreateClient(clientName);

            using var response = await client.GetAsync(videoUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var fs = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await response.Content.CopyToAsync(fs, cancellationToken);

            return destinationPath;
        }

        #region Douyin Extraction Logic

        private async Task<MediaExtractResult> ExtractDouyinAsync(string input, CancellationToken cancellationToken)
        {
            try
            {
                var douyinClient = _httpClientFactory.CreateClient("DouyinClient");

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
                        return new MediaExtractResult { Success = false, ErrorMessage = "Không tìm thấy link Douyin hợp lệ" };
                    }

                    string shortUrl = shortLinkMatch.Value;
                    using var reqRedirect = new HttpRequestMessage(HttpMethod.Get, shortUrl);
                    using var redirectRes = await douyinClient.SendAsync(reqRedirect, cancellationToken);

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
                    return new MediaExtractResult { Success = false, ErrorMessage = "Không thể trích xuất Video ID từ link Douyin" };
                }

                // 2. Lấy cookie ttwid
                string ttwidCookie;
                lock (_ttwidLock)
                {
                    ttwidCookie = _cachedTtwid;
                }
                if (string.IsNullOrEmpty(ttwidCookie))
                {
                    ttwidCookie = await GetTtwidCookieAsync(douyinClient);
                    if (!string.IsNullOrEmpty(ttwidCookie))
                    {
                        lock (_ttwidLock)
                        {
                            _cachedTtwid = ttwidCookie;
                        }
                    }
                }

                // 3. Tải nội dung trang chi tiết iesdouyin.com
                string detailUrl = $"https://www.iesdouyin.com/share/video/{videoId}/";
                string html = await FetchDetailHtmlAsync(douyinClient, detailUrl, ttwidCookie);

                if (!html.Contains("videoInfoRes"))
                {
                    string freshTtwid = await GetTtwidCookieAsync(douyinClient);
                    if (!string.IsNullOrEmpty(freshTtwid))
                    {
                        lock (_ttwidLock)
                        {
                            _cachedTtwid = freshTtwid;
                        }
                        html = await FetchDetailHtmlAsync(douyinClient, detailUrl, freshTtwid);
                    }
                }

                string title = "";
                string videoUrl = "";
                bool isImageAlbum = false;

                // 4. Bóc tách JSON từ _ROUTER_DATA
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
                                if (prop.Value.ValueKind == JsonValueKind.Object &&
                                    prop.Value.TryGetProperty("videoInfoRes", out var infoRes) &&
                                    infoRes.TryGetProperty("item_list", out var itemList) &&
                                    itemList.GetArrayLength() > 0)
                                {
                                    var item = itemList[0];

                                    if (item.TryGetProperty("desc", out var descProp))
                                    {
                                        title = descProp.GetString() ?? "";
                                    }

                                    if (item.TryGetProperty("images", out var imagesProp) &&
                                        imagesProp.ValueKind == JsonValueKind.Array &&
                                        imagesProp.GetArrayLength() > 0)
                                    {
                                        isImageAlbum = true;
                                    }

                                    if (item.TryGetProperty("video", out var vProp))
                                    {
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

                                        string cleanPlayUrl = !string.IsNullOrEmpty(playUri)
                                            ? $"https://aweme.snssdk.com/aweme/v1/play/?video_id={playUri}&ratio=1080p&line=0"
                                            : playWmUrl.Replace("playwm", "play").Replace("ratio=720p", "ratio=1080p");

                                        string directCdnUrl = await ResolveDirectCdnUrlAsync(cleanPlayUrl);
                                        videoUrl = !string.IsNullOrEmpty(directCdnUrl) ? directCdnUrl : cleanPlayUrl;
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

                if (string.IsNullOrEmpty(videoUrl))
                {
                    var cdnMatch = Regex.Match(html, @"https?://[^""'\s\\]+?(?:douyinvod|snssdk|douyinpic)[^""'\s\\]*?\.mp4[^""'\s]*");
                    if (cdnMatch.Success)
                    {
                        videoUrl = cdnMatch.Value.Replace(@"\/", "/").Replace(@"\", "");
                    }
                }

                if (string.IsNullOrEmpty(title))
                {
                    var titleMatch = Regex.Match(html, @"<title>(.*?)</title>");
                    if (titleMatch.Success)
                    {
                        title = HttpUtility.HtmlDecode(titleMatch.Groups[1].Value.Trim());
                    }
                }

                if (isImageAlbum)
                {
                    return new MediaExtractResult
                    {
                        Success = true,
                        Platform = "douyin",
                        Title = title,
                        IsImageAlbum = true,
                        ErrorMessage = "Bài viết Douyin này là album ảnh, không phải video."
                    };
                }

                if (string.IsNullOrEmpty(videoUrl))
                {
                    return new MediaExtractResult { Success = false, ErrorMessage = "Không tìm thấy link video Douyin từ URL được cung cấp" };
                }

                return new MediaExtractResult
                {
                    Success = true,
                    Platform = "douyin",
                    Title = title,
                    VideoUrl = videoUrl,
                    IsImageAlbum = false
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi trích xuất Douyin: {Url}", input);
                return new MediaExtractResult { Success = false, ErrorMessage = ex.Message };
            }
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

        private async Task<string> GetTtwidCookieAsync(HttpClient client)
        {
            // Cách 1: Đăng ký ttwid qua API chính thức của ByteDance (nhanh và chuẩn xác nhất)
            try
            {
                using var regReq = new HttpRequestMessage(HttpMethod.Post, "https://ttwid.bytedance.com/ttwid/union/register/");
                regReq.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

                var payload = new
                {
                    region = "cn",
                    aid = 1768,
                    needFid = false,
                    service = "www.ixigua.com",
                    migrate_info = new { ticket = "", source = "node" },
                    cbUrlProtocol = "https",
                    union = true
                };
                regReq.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                using var regRes = await client.SendAsync(regReq);
                if (regRes.Headers.TryGetValues("Set-Cookie", out var cookieHeaders))
                {
                    foreach (var header in cookieHeaders)
                    {
                        var match = Regex.Match(header, @"ttwid=([^;]+)");
                        if (match.Success)
                        {
                            _logger.LogInformation("Lấy ttwid thành công qua ttwid.bytedance.com: {Ttwid}", match.Groups[1].Value);
                            return match.Groups[1].Value;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Không thể lấy cookie ttwid từ ttwid.bytedance.com, thử dự phòng");
            }

            // Cách 2: Dự phòng lấy từ trang chủ douyin.com
            try
            {
                using var homeReq = new HttpRequestMessage(HttpMethod.Get, "https://www.douyin.com/");
                homeReq.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                using var homeRes = await client.SendAsync(homeReq);
                if (homeRes.Headers.TryGetValues("Set-Cookie", out var homeCookies))
                {
                    foreach (var header in homeCookies)
                    {
                        var match = Regex.Match(header, @"ttwid=([^;]+)");
                        if (match.Success)
                        {
                            return match.Groups[1].Value;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Không thể lấy cookie ttwid từ douyin.com");
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

        #endregion

        #region Rednote Extraction Logic

        private async Task<MediaExtractResult> ExtractRednoteAsync(string input, CancellationToken cancellationToken)
        {
            try
            {
                var urlMatch = Regex.Match(input, @"https?://[^\s\u4e00-\u9fa5""<>]+");
                if (!urlMatch.Success)
                {
                    return new MediaExtractResult { Success = false, ErrorMessage = "Không tìm thấy link Rednote hợp lệ" };
                }

                string targetUrl = urlMatch.Value.TrimEnd('.', ',', '，', '。', ';', '；', '!', '！');

                // Xử lý link rút gọn xhslink.com
                if (targetUrl.Contains("xhslink.com", StringComparison.OrdinalIgnoreCase))
                {
                    using var redirectHandler = new HttpClientHandler { AllowAutoRedirect = false };
                    using var redirectClient = new HttpClient(redirectHandler) { Timeout = TimeSpan.FromSeconds(10) };
                    redirectClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36");

                    var headResp = await redirectClient.GetAsync(targetUrl, cancellationToken);
                    var location = headResp.Headers.Location?.ToString();
                    if (!string.IsNullOrEmpty(location))
                    {
                        targetUrl = location.StartsWith("/") ? $"https://www.xiaohongshu.com{location}" : location;
                    }
                }

                var discoveryMatch = Regex.Match(targetUrl, @"/discovery/item/([a-zA-Z0-9]+)");
                if (discoveryMatch.Success)
                {
                    string noteId = discoveryMatch.Groups[1].Value;
                    var tokenMatch = Regex.Match(targetUrl, @"xsec_token=([^&]+)");
                    string tokenParam = tokenMatch.Success ? $"?xsec_token={tokenMatch.Groups[1].Value}" : "";
                    targetUrl = $"https://www.xiaohongshu.com/explore/{noteId}{tokenParam}";
                }

                var client = _httpClientFactory.CreateClient("RednoteClient");
                var response = await client.GetAsync(targetUrl, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    return new MediaExtractResult { Success = false, ErrorMessage = $"Không thể truy cập Rednote: {response.StatusCode}" };
                }

                var rawBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                var htmlContent = System.Text.Encoding.UTF8.GetString(rawBytes);

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

                                    if (noteData.TryGetProperty("video", out var videoProp))
                                    {
                                        string? videoUrl = null;

                                        if (videoProp.TryGetProperty("consumer", out var consumer) &&
                                            consumer.TryGetProperty("originVideoKey", out var originKey) &&
                                            !string.IsNullOrEmpty(originKey.GetString()))
                                        {
                                            videoUrl = $"https://sns-video-bd.xhscdn.com/{originKey.GetString()}";
                                        }

                                        if (string.IsNullOrEmpty(videoUrl) &&
                                            videoProp.TryGetProperty("media", out var media) &&
                                            media.TryGetProperty("stream", out var stream) &&
                                            stream.TryGetProperty("h264", out var h264List) &&
                                            h264List.GetArrayLength() > 0)
                                        {
                                            videoUrl = h264List[0].GetProperty("masterUrl").GetString();
                                        }

                                        if (!string.IsNullOrEmpty(videoUrl))
                                        {
                                            return new MediaExtractResult
                                            {
                                                Success = true,
                                                Platform = "rednote",
                                                Title = displayTitle,
                                                VideoUrl = videoUrl,
                                                IsImageAlbum = false
                                            };
                                        }
                                    }

                                    // Nếu là album ảnh
                                    if (noteData.TryGetProperty("imageList", out var imgList) && imgList.GetArrayLength() > 0)
                                    {
                                        return new MediaExtractResult
                                        {
                                            Success = true,
                                            Platform = "rednote",
                                            Title = displayTitle,
                                            IsImageAlbum = true,
                                            ErrorMessage = "Bài viết Rednote này là album ảnh, không phải video."
                                        };
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Phân tích __INITIAL_STATE__ Rednote thất bại, thử regex fallback.");
                    }
                }

                // Fallback Regex
                var match = Regex.Match(htmlContent, @"https?:\\?/\\?/sns-video[^""'\s\\]+");
                if (!match.Success)
                {
                    match = Regex.Match(htmlContent, @"https?:\\?/\\?/[^""'\s\\]+\.mp4[^""'\s]*");
                }

                if (match.Success)
                {
                    string cleanVideoUrl = match.Value.Replace(@"\/", "/").Replace(@"\", "");
                    return new MediaExtractResult
                    {
                        Success = true,
                        Platform = "rednote",
                        Title = "Rednote Video",
                        VideoUrl = cleanVideoUrl,
                        IsImageAlbum = false
                    };
                }

                return new MediaExtractResult
                {
                    Success = false,
                    ErrorMessage = "Không tìm thấy video hợp lệ trong bài viết Rednote này."
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi trích xuất Rednote: {Url}", input);
                return new MediaExtractResult { Success = false, ErrorMessage = ex.Message };
            }
        }

        #endregion
    }
}
