# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:9.0-alpine AS build
WORKDIR /src

# Copy csproj and restore
COPY ["OCREngine.csproj", "./"]
RUN dotnet restore "OCREngine.csproj"

# Copy everything else and build
COPY . .
RUN dotnet build "OCREngine.csproj" -c Release -o /app/build

# Stage 2: Publish
FROM build AS publish
RUN dotnet publish "OCREngine.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Stage 3: Final Runtime
FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine AS final
WORKDIR /app

# Install native dependencies for SkiaSharp and image processing on Alpine
RUN apk add --no-cache \
    icu-libs \
    fontconfig \
    freetype \
    libpng \
    libjpeg-turbo \
    libstdc++ \
    libgcc

# Set environment variables for globalization
# SkiaSharp on Alpine requires setting specific env or using NoDependencies build
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    LC_ALL=en_US.UTF-8 \
    LANG=en_US.UTF-8 \
    ASPNETCORE_URLS=http://+:80

# Copy published files
COPY --from=publish /app/publish .

# Expose port
EXPOSE 80

# Start application
ENTRYPOINT ["dotnet", "OCREngine.dll"]
