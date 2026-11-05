using System.Collections.Concurrent;

namespace RednoteDownloaderApi.Services
{
    public class CachedVideoItem
    {
        public string VideoId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Platform { get; set; } = string.Empty;
        public string OriginalFilePath { get; set; } = string.Empty;
        public string? ProcessedFilePath { get; set; }
        public bool HasChinese { get; set; }
        public List<string> DetectedSamples { get; set; } = new();
        public List<string>? SuggestedHooks { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    public interface IVideoCacheManager
    {
        void AddOrUpdate(CachedVideoItem item);
        CachedVideoItem? Get(string videoId);
        bool Remove(string videoId);
        void DeleteVideoFiles(string videoId);
        IEnumerable<CachedVideoItem> GetAllExpired(TimeSpan expiration);
    }

    public class VideoCacheManager : IVideoCacheManager
    {
        private readonly ConcurrentDictionary<string, CachedVideoItem> _cache = new();
        private readonly ILogger<VideoCacheManager> _logger;

        public VideoCacheManager(ILogger<VideoCacheManager> logger)
        {
            _logger = logger;
        }

        public void AddOrUpdate(CachedVideoItem item)
        {
            _cache[item.VideoId] = item;
        }

        public CachedVideoItem? Get(string videoId)
        {
            _cache.TryGetValue(videoId, out var item);
            return item;
        }

        public bool Remove(string videoId)
        {
            return _cache.TryRemove(videoId, out _);
        }

        public void DeleteVideoFiles(string videoId)
        {
            if (_cache.TryRemove(videoId, out var item))
            {
                try
                {
                    if (!string.IsNullOrEmpty(item.OriginalFilePath) && File.Exists(item.OriginalFilePath))
                    {
                        File.Delete(item.OriginalFilePath);
                        _logger.LogInformation("Đã xóa file video gốc đệm: {Path}", item.OriginalFilePath);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Lỗi khi xóa file gốc: {Path}", item.OriginalFilePath);
                }

                try
                {
                    if (!string.IsNullOrEmpty(item.ProcessedFilePath) && File.Exists(item.ProcessedFilePath))
                    {
                        File.Delete(item.ProcessedFilePath);
                        _logger.LogInformation("Đã xóa file video đã xử lý: {Path}", item.ProcessedFilePath);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Lỗi khi xóa file xử lý: {Path}", item.ProcessedFilePath);
                }
            }
        }

        public IEnumerable<CachedVideoItem> GetAllExpired(TimeSpan expiration)
        {
            var cutoff = DateTime.UtcNow - expiration;
            return _cache.Values.Where(v => v.CreatedAt < cutoff).ToList();
        }
    }
}

