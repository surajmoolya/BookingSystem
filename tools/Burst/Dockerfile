# The burst tool as an image, for machines without the .NET SDK (burst.sh falls back to it).
#   docker build -t seatres-burst -f tools/Burst/Dockerfile .
#   docker run --rm --network host seatres-burst <BASE_URL> [options]
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY tools/Burst/Burst.csproj tools/Burst/
RUN dotnet restore tools/Burst/Burst.csproj

COPY tools/Burst/ tools/Burst/
RUN dotnet publish tools/Burst/Burst.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /app .
# burst.sh runs it as the calling user with the current directory at /work, so a relative --json path lands there.
WORKDIR /work
ENTRYPOINT ["dotnet", "/app/Burst.dll"]
