# Multi-stage build for the VINTAGE FADES booking service (.NET 10 LTS).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props VintageFades.slnx ./
COPY src/ src/
RUN dotnet publish src/VintageFades.Web/VintageFades.Web.csproj -c Release -o /app/publish /p:UseAppHost=false

# Optional target: EF Core migration bundle, a single executable that applies pending migrations.
#   docker build --target migrations -t vintagefades-migrations .
#   docker run --rm vintagefades-migrations --connection "<connection string>"
FROM build AS migrations
RUN dotnet tool install --global dotnet-ef --version 10.0.14 \
 && /root/.dotnet/tools/dotnet-ef migrations bundle --project src/VintageFades.Infrastructure \
      --startup-project src/VintageFades.Infrastructure --self-contained -r linux-x64 -o /efbundle
ENTRYPOINT ["/efbundle"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
# ICU ships in the aspnet image, so the IANA time zone Africa/Windhoek resolves correctly.
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production
COPY --from=build /app/publish .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "VintageFades.Web.dll"]
