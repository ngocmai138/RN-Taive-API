namespace RednoteDownloaderApi.Services
{
    public class TempCleanupBackgroundService : BackgroundService
    {
        private readonly IVideoCacheManager _cacheManager;
        private readonly IConfiguration _configuration;
        private readonly ILogger<TempCleanupBackgroundService> _logger;

        public TempCleanupBackgroundService(
            IVideoCacheManager cacheManager,
            IConfiguration configuration,
            ILogger<TempCleanupBackgroundService> logger)
        {
            _cacheManager = cacheManager;
            _configuration = configuration;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("TempCleanupBackgroundService đã khởi động.");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Quét các video trong cache đã quá 15 phút mà người dùng không tải
                    var expiredVideos = _cacheManager.GetAllExpired(TimeSpan.FromMinutes(15));
                    foreach (var video in expiredVideos)
                    {
                        _logger.LogInformation("Tự động dọn dẹp video hết hạn ({VideoId})", video.VideoId);
                        _cacheManager.DeleteVideoFiles(video.VideoId);
                    }

                    // Quét thêm thư mục TempVideos nếu có file mồ côi > 30 phút
                    string tempDir = _configuration["VideoProcessing:TempDir"] ?? "TempVideos";
                    if (Directory.Exists(tempDir))
                    {
                        var files = Directory.GetFiles(tempDir);
                        foreach (var file in files)
                        {
                            var fi = new FileInfo(file);
                            if (fi.CreationTimeUtc < DateTime.UtcNow.AddMinutes(-30))
                            {
                                try
                                {
                                    fi.Delete();
                                    _logger.LogInformation("Đã dọn dẹp file tạm mồ côi: {File}", file);
                                }
                                catch
                                {
                                    // Bỏ qua nếu file đang bị lock
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi xảy ra trong vòng lặp dọn dẹp file tạm.");
                }

                // Chạy kiểm tra mỗi 10 phút
                await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);
            }
        }
    }
}

