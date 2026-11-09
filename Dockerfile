# CONSTRUCT DPR server (ASP.NET Core) with the web app inside.
#   docker build -t construct-dpr .
#   docker run -p 8080:8080 -e DATABASE_URL=postgres://user:pass@host:5432/db construct-dpr
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/ConstructDpr.Server/ConstructDpr.Server.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# Fonts and fontconfig for PDF reports (QuestPDF / Skia).
RUN apt-get update && apt-get install -y --no-install-recommends libfontconfig1 fonts-dejavu-core && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
# SQLite file (only used when no DATABASE_URL is given) lives in /app/data: mount a volume there.
RUN mkdir -p /app/data && chown -R app:app /app/data
USER app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=40s CMD wget -qO- http://localhost:8080/health || exit 1
ENTRYPOINT ["dotnet", "ConstructDpr.Server.dll"]
