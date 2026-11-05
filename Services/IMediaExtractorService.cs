namespace RednoteDownloaderApi.Services
{
    public class MediaExtractResult
    {
        public bool Success { get; set; }
        public string Platform { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string? VideoUrl { get; set; }
        public bool IsImageAlbum { get; set; }
        public string? ErrorMessage { get; set; }
    }

    public interface IMediaExtractorService
    {
        Task<MediaExtractResult> ExtractMediaAsync(string inputUrl, CancellationToken cancellationToken = default);
        Task<string> DownloadVideoToTempAsync(string videoUrl, string platform, string destinationPath, CancellationToken cancellationToken = default);
    }
}

