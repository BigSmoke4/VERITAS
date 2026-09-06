FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY Veritas.sln .
COPY src/Veritas.Web/Veritas.Web.csproj src/Veritas.Web/
COPY tests/Veritas.Tests/Veritas.Tests.csproj tests/Veritas.Tests/
RUN dotnet restore Veritas.sln
COPY . .
RUN dotnet publish src/Veritas.Web/Veritas.Web.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Veritas.Web.dll"]
