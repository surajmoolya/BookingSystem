FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore first, from the project files only, so the restore layer stays cached until a csproj changes.
COPY Directory.Build.props ./
COPY src/SeatReservation.Api/*.csproj src/SeatReservation.Api/
COPY src/SeatReservation.Application/*.csproj src/SeatReservation.Application/
COPY src/SeatReservation.Infrastructure/*.csproj src/SeatReservation.Infrastructure/
# ReadyToRun needs the crossgen package, so restore with the same RID and PublishReadyToRun as publish.
RUN dotnet restore src/SeatReservation.Api/SeatReservation.Api.csproj -r linux-x64 -p:PublishReadyToRun=true

COPY src/ src/
RUN dotnet publish src/SeatReservation.Api/SeatReservation.Api.csproj -c Release -o /app \
    -r linux-x64 --self-contained false -p:PublishReadyToRun=true --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app .

# Program.cs binds http://0.0.0.0:$PORT; Render overrides PORT, locally it stays 8080.
# ASPNETCORE_HTTP_PORTS (set by the base image) is cleared so Kestrel does not warn that PORT overrides it.
ENV ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_TieredPGO=1 \
    PORT=8080 \
    ASPNETCORE_HTTP_PORTS=
EXPOSE 8080

# Non-root user built into the .NET 8 images.
USER app
ENTRYPOINT ["dotnet", "SeatReservation.Api.dll"]
