using System.Diagnostics;
using System.Text.Json;

namespace RednoteDownloaderApi.Services
{
    public class VideoProcessingService : IVideoProcessingService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<VideoProcessingService> _logger;

        public VideoProcessingService(IConfiguration configuration, ILogger<VideoProcessingService> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        public async Task<CheckChineseResult> CheckChineseAsync(string videoPath, CancellationToken cancellationToken = default)
        {
            string pythonExe = _configuration["VideoProcessing:PythonExecutable"] ?? "python";
            string scriptPath = _configuration["VideoProcessing:ScriptPath"] ?? "VideoProcessor/video_processor.py";

            string fullScriptPath = Path.IsPathRooted(scriptPath)
                ? scriptPath
                : Path.Combine(Directory.GetCurrentDirectory(), scriptPath);

            var psi = new ProcessStartInfo
            {
                FileName = pythonExe,
                Arguments = $"\"{fullScriptPath}\" --action check --input \"{videoPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            try
            {
                using var process = new Process { StartInfo = psi };
                process.Start();

                var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
                var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

                await process.WaitForExitAsync(cancellationToken);

                string output = await outputTask;
                string error = await errorTask;

                if (!string.IsNullOrWhiteSpace(error))
                {
                    _logger.LogWarning("Python script stderr: {Error}", error);
                }

                if (!string.IsNullOrWhiteSpace(output))
                {
                    // Lấy dòng JSON cuối cùng
                    var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in lines.Reverse())
                    {
                        if (line.Trim().StartsWith("{") && line.Trim().EndsWith("}"))
                        {
                            var parsed = JsonSerializer.Deserialize<CheckChineseResult>(line.Trim());
                            if (parsed != null) return parsed;
                        }
                    }
                }

                return new CheckChineseResult
                {
                    Status = "error",
                    Message = !string.IsNullOrWhiteSpace(error) ? error : "Không nhận được phản hồi hợp lệ từ Python engine"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi chạy kiểm tra chữ tiếng Trung trên video: {VideoPath}", videoPath);
                return new CheckChineseResult { Status = "error", Message = ex.Message };
            }
        }

        public async Task<ProcessVideoResult> ProcessVideoAsync(string inputPath, string outputPath, string mode, string? hookText = null, CancellationToken cancellationToken = default)
        {
            string pythonExe = _configuration["VideoProcessing:PythonExecutable"] ?? "python";
            string scriptPath = _configuration["VideoProcessing:ScriptPath"] ?? "VideoProcessor/video_processor.py";
            string apiKey = _configuration["Gemini:ApiKey"] ?? "";
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? Environment.GetEnvironmentVariable("Gemini__ApiKey") ?? "";
            }

            string fullScriptPath = Path.IsPathRooted(scriptPath)
                ? scriptPath
                : Path.Combine(Directory.GetCurrentDirectory(), scriptPath);

            string args = $"\"{fullScriptPath}\" --action {mode} --input \"{inputPath}\" --output \"{outputPath}\" --api-key \"{apiKey}\"";
            if (!string.IsNullOrWhiteSpace(hookText))
            {
                string escapedHook = hookText.Replace("\"", "\\\"");
                args += $" --hook-text \"{escapedHook}\"";
            }

            var psi = new ProcessStartInfo
            {
                FileName = pythonExe,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            try
            {
                using var process = new Process { StartInfo = psi };
                process.Start();

                var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
                var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

                await process.WaitForExitAsync(cancellationToken);

                string output = await outputTask;
                string error = await errorTask;

                if (!string.IsNullOrWhiteSpace(error))
                {
                    _logger.LogWarning("Python process stderr ({Mode}): {Error}", mode, error);
                }

                if (!string.IsNullOrWhiteSpace(output))
                {
                    var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in lines.Reverse())
                    {
                        if (line.Trim().StartsWith("{") && line.Trim().EndsWith("}"))
                        {
                            var parsed = JsonSerializer.Deserialize<ProcessVideoResult>(line.Trim());
                            if (parsed != null) return parsed;
                        }
                    }
                }

                return new ProcessVideoResult
                {
                    Status = "error",
                    Message = !string.IsNullOrWhiteSpace(error) ? error : "Không nhận được phản hồi xử lý từ Python engine"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi xử lý video ({Mode}): {InputPath}", mode, inputPath);
                return new ProcessVideoResult { Status = "error", Message = ex.Message };
            }
        }

        public async Task<List<string>> GenerateHooksAsync(string videoPath, CancellationToken cancellationToken = default)
        {
            string pythonExe = _configuration["VideoProcessing:PythonExecutable"] ?? "python";
            string scriptPath = _configuration["VideoProcessing:ScriptPath"] ?? "VideoProcessor/video_processor.py";
            string apiKey = _configuration["Gemini:ApiKey"] ?? "";
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? Environment.GetEnvironmentVariable("Gemini__ApiKey") ?? "";
            }

            string fullScriptPath = Path.IsPathRooted(scriptPath)
                ? scriptPath
                : Path.Combine(Directory.GetCurrentDirectory(), scriptPath);

            var psi = new ProcessStartInfo
            {
                FileName = pythonExe,
                Arguments = $"\"{fullScriptPath}\" --action generate_hooks --input \"{videoPath}\" --api-key \"{apiKey}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            try
            {
                using var process = new Process { StartInfo = psi };
                process.Start();

                var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
                var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

                await process.WaitForExitAsync(cancellationToken);

                string output = await outputTask;
                string error = await errorTask;

                if (!string.IsNullOrWhiteSpace(error))
                {
                    _logger.LogWarning("Python generate_hooks stderr: {Error}", error);
                }

                if (!string.IsNullOrWhiteSpace(output))
                {
                    var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in lines.Reverse())
                    {
                        if (line.Trim().StartsWith("{") && line.Trim().EndsWith("}"))
                        {
                            var parsed = JsonSerializer.Deserialize<GenerateHooksResult>(line.Trim());
                            if (parsed != null && parsed.Hooks != null && parsed.Hooks.Count > 0)
                            {
                                return parsed.Hooks;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi gọi AI gợi ý Hook: {VideoPath}", videoPath);
            }

            // Fallback gợi ý viral mặc định nếu AI gặp sự cố
            return new List<string>
            {
                "Wait until you see how this turns out ✨",
                "You will never believe how easy this is 🤯",
                "The ultimate hack you wish you knew earlier 💡",
                "Stop scrolling! You need to see this right now 🔥"
            };
        }
    }
}

