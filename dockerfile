FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
RUN apt-get update && apt-get install -y --no-install-recommends git \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/HgvMate.Mcp/HgvMate.Mcp.csproj -c Release -o /app

FROM base
COPY --from=build /app .
VOLUME /data
ENTRYPOINT ["dotnet", "HgvMate.Mcp.dll"]
