# Use a base image without .NET pre-installed so we can control the exact
# SDK version via the devcontainer dotnet feature (avoids stale RC builds).
FROM mcr.microsoft.com/devcontainers/base:ubuntu

# The default dotnet/sdk image sets NUGET_XMLDOC_MODE=skip to save space.
# Clear it so NuGet XML docs are extracted (richer IntelliSense tooltips).
ENV NUGET_XMLDOC_MODE=
