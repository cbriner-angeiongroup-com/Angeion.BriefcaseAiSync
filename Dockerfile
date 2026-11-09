FROM mcr.microsoft.com/dotnet/sdk:10.0.301 AS build
WORKDIR /source
COPY global.json Directory.Build.props ./
COPY src/ src/
COPY config/ config/
RUN dotnet restore src/BriefcaseSync.Worker --locked-mode
RUN dotnet publish src/BriefcaseSync.Worker -c Release --no-restore -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0.14
WORKDIR /app
COPY --from=build /app ./
USER $APP_UID
ENTRYPOINT ["dotnet", "BriefcaseSync.Worker.dll"]
