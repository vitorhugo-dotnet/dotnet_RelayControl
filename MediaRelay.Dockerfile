# syntax=docker/dockerfile:1.7

ARG DOTNET_MAJOR=10.0
ARG APP_PROJECT=services/SonicRelay.MediaRelay/SonicRelay.MediaRelay.csproj

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_MAJOR} AS build
ARG APP_PROJECT
WORKDIR /src

COPY . .
RUN dotnet restore "$APP_PROJECT"
RUN dotnet publish "$APP_PROJECT" \
    --configuration Release \
    --no-restore \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_MAJOR} AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

COPY --from=build /app/publish ./
USER $APP_UID

ENTRYPOINT ["dotnet", "SonicRelay.MediaRelay.dll"]
