FROM mcr.microsoft.com/dotnet/sdk:8.0-bookworm-slim AS build
WORKDIR /source
COPY src/TempBin/TempBin.csproj src/TempBin/
RUN dotnet restore src/TempBin/TempBin.csproj
COPY src/ src/
RUN dotnet publish src/TempBin/TempBin.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080 DOTNET_EnableDiagnostics=0
USER $APP_UID
EXPOSE 8080
# The runtime image intentionally has no curl/wget; use a tiny .NET health probe.
ENTRYPOINT ["dotnet", "TempBin.dll"]
