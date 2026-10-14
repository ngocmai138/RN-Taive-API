using Scalar.AspNetCore;
using System.Text;

// Đăng ký CodePagesEncodingProvider để hỗ trợ bảng mã gbk, gb2312 của Rednote/Xiaohongshu
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddOpenApi();

// 1. Cấu hình CORS cho phép các website frontend gọi API
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

// 2. Đăng ký HttpClient tối ưu hóa kết nối, tự động chuyển hướng và quản lý Cookie
builder.Services.AddHttpClient("RednoteClient", client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
    client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36");
    client.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
    client.DefaultRequestHeaders.Add("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    AllowAutoRedirect = true,
    UseCookies = true
});

// 3. Đăng ký HttpClient riêng cho Proxy tải file từ CDN
builder.Services.AddHttpClient("CdnProxyClient", client =>
{
    client.Timeout = TimeSpan.FromMinutes(2);
    client.DefaultRequestHeaders.Add("Referer", "https://www.xiaohongshu.com/");
    client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
});

var app = builder.Build();

// Cấu hình Swagger UI xuất hiện ngay tại trang chủ localhost:5025/
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Rednote Downloader API v1");
    c.RoutePrefix = string.Empty; // Hiển thị Swagger UI tại root (localhost:5025)
});

// Điều hướng /swagger về trang chủ
app.MapGet("/swagger", () => Results.Redirect("/"));

// Configure OpenAPI & Scalar API Reference
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseCors("AllowAll");

if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseAuthorization();

app.MapControllers();

app.Run();

