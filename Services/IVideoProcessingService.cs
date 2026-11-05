using System.Text.Json.Serialization;

namespace RednoteDownloaderApi.Services
{
    public class CheckChineseResult
    {
        [JsonPropertyName("status")]
        public string Status { get; set; } = "error";

        [JsonPropertyName("has_chinese")]
        public bool HasChinese { get; set; }

        [JsonPropertyName("sample_count")]
        public int SampleCount { get; set; }

        [JsonPropertyName("detected_samples")]
        public List<string>? DetectedSamples { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }

    public class ProcessVideoResult
    {
        [JsonPropertyName("status")]
        public string Status { get; set; } = "error";

        [JsonPropertyName("mode")]
        public string? Mode { get; set; }

        [JsonPropertyName("output_path")]
        public string? OutputPath { get; set; }

        [JsonPropertyName("total_frames")]
        public int TotalFrames { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }

    public class GenerateHooksResult
    {
        [JsonPropertyName("status")]
        public string Status { get; set; } = "error";

        [JsonPropertyName("hooks")]
        public List<string>? Hooks { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }

    public interface IVideoProcessingService
    {
        Task<CheckChineseResult> CheckChineseAsync(string videoPath, CancellationToken cancellationToken = default);
        Task<ProcessVideoResult> ProcessVideoAsync(string inputPath, string outputPath, string mode, string? hookText = null, CancellationToken cancellationToken = default);
        Task<List<string>> GenerateHooksAsync(string videoPath, CancellationToken cancellationToken = default);
    }
}

