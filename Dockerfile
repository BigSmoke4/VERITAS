FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY Veritas.sln .
# Every project referenced by the solution must be present before `dotnet restore`
# can resolve the graph — omitting one makes the restore fail with MSB3202.
COPY src/Veritas.Web/Veritas.Web.csproj src/Veritas.Web/
COPY tests/Veritas.Tests/Veritas.Tests.csproj tests/Veritas.Tests/
COPY tests/Veritas.IntegrationTests/Veritas.IntegrationTests.csproj tests/Veritas.IntegrationTests/
COPY tests/Veritas.Benchmarks/Veritas.Benchmarks.csproj tests/Veritas.Benchmarks/
RUN dotnet restore Veritas.sln
COPY . .
RUN dotnet publish src/Veritas.Web/Veritas.Web.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0 \
    ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080

# Drop privileges: the container must not run as root.
RUN groupadd --system veritas --gid 1001 \
 && useradd --system --uid 1001 --gid veritas --home /app veritas \
 && chown -R veritas:veritas /app
USER veritas

HEALTHCHECK --interval=30s --timeout=5s --start-period=40s --retries=3 \
  CMD ["/bin/sh", "-c", "wget -qO- http://127.0.0.1:8080/health/live || exit 1"]

ENTRYPOINT ["dotnet", "Veritas.Web.dll"]
