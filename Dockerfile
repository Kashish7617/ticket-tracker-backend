# Build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first so the package layer is cached until the project file changes.
COPY src/TicketTracker.Api/TicketTracker.Api.csproj src/TicketTracker.Api/
RUN dotnet restore src/TicketTracker.Api/TicketTracker.Api.csproj

COPY src/ src/
RUN dotnet publish src/TicketTracker.Api/TicketTracker.Api.csproj -c Release -o /app --no-restore

# Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

# Non-root user provided by the official image.
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "TicketTracker.Api.dll"]
