# Giai đoạn Build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy file csproj và restore dependencies trước để tận dụng Docker cache
COPY ["AutoHubApi.csproj", "./"]
RUN dotnet restore "./AutoHubApi.csproj"

# Copy toàn bộ mã nguồn và publish
COPY . .
RUN dotnet publish "./AutoHubApi.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Giai đoạn Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Cài đặt Python3, pip, ffmpeg và các thư viện cần thiết
RUN apt-get update && apt-get install -y --no-install-recommends \
    python3 \
    python3-pip \
    ffmpeg \
    libgl1 \
    libglib2.0-0 \
    fonts-comic-neue \
    fonts-dejavu-core \
    && rm -rf /var/lib/apt/lists/*

# Cài đặt các package Python cho OCR, xử lý ảnh và video
RUN pip3 install --no-cache-dir --break-system-packages \
    opencv-python-headless \
    pillow \
    numpy \
    rapidocr-onnxruntime \
    requests

COPY --from=build /app/publish .

# Thiết lập cổng mặc định 8080 cho Render / Cloud và cấu hình python3
ENV ASPNETCORE_HTTP_PORTS=8080
ENV VideoProcessing__PythonExecutable=python3
EXPOSE 8080

ENTRYPOINT ["dotnet", "AutoHubApi.dll"]
