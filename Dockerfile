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
COPY --from=build /app/publish .

# Thiết lập cổng mặc định 8080 cho Render / Cloud
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "RednoteDownloaderApi.dll"]
