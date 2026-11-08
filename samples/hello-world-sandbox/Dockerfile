# Build from the repository root:
#   docker buildx build . --platform linux/amd64 -f samples/hello-world-sandbox/Dockerfile -t <you>/hello-world-sandbox-agent:rc3 --push

# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore dependencies (cached if the project file hasn't changed)
COPY samples/hello-world-sandbox/HelloWorldSandbox.csproj samples/hello-world-sandbox/
RUN dotnet restore samples/hello-world-sandbox/HelloWorldSandbox.csproj

# Copy source code and protos
COPY samples/hello-world-sandbox/ samples/hello-world-sandbox/

# Build and publish
WORKDIR /src/samples/hello-world-sandbox
RUN dotnet publish HelloWorldSandbox.csproj -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish .

# kagent sandbox contract: private A2A gRPC on runtime.port (80) and readiness on 8081.
# Kestrel listeners are configured in code, so ASPNETCORE_URLS / ASPNETCORE_HTTP_PORTS are unused.
EXPOSE 80 8081

ENTRYPOINT ["dotnet", "/app/HelloWorldSandbox.dll"]
