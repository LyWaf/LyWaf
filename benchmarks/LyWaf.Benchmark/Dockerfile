FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY LyWaf.Benchmark.csproj .
RUN dotnet publish -c Release -o /app --nologo

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "LyWaf.Benchmark.dll", "serve", "--urls", "http://0.0.0.0:19080"]
