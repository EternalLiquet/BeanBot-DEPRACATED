FROM mcr.microsoft.com/dotnet/sdk:10.0-noble@sha256:e1ffd2a92ae84c1291bc1b6887501f8af98e6331e7af6d4c8d37168c5e87a64c AS build
WORKDIR /src

ARG BEANBOT_VERSION=0.0.0-local
ARG BEANBOT_COMMIT_SHA=unknown

COPY ["Directory.Build.props", "Directory.Packages.props", "global.json", "./"]
COPY ["BeanBot/BeanBot.csproj", "BeanBot/packages.lock.json", "BeanBot/"]
RUN dotnet restore "BeanBot/BeanBot.csproj" --locked-mode

COPY . .
WORKDIR /src/BeanBot
RUN dotnet publish "BeanBot.csproj" -c Release -o /app/publish --no-restore \
    -p:BeanBotReleaseVersion="$BEANBOT_VERSION" \
    -p:BeanBotCommitSha="$BEANBOT_COMMIT_SHA"
RUN test -s /app/publish/Resources/puns.csv \
    && mkdir -p /app/publish/BeanBotFiles/Logs

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra@sha256:f5b3b2e2e548828d50e349726f51a5de001286f02c4bbde77db0dd34eb9f55ff AS final
WORKDIR /app

ARG BEANBOT_VERSION=0.0.0-local
ARG BEANBOT_COMMIT_SHA=unknown

LABEL org.opencontainers.image.source="https://github.com/EternalLiquet/BeanBot-DEPRACATED" \
      org.opencontainers.image.version="$BEANBOT_VERSION" \
      org.opencontainers.image.revision="$BEANBOT_COMMIT_SHA"

COPY --from=build --chown=$APP_UID:$APP_UID /app/publish ./

VOLUME ["/app/BeanBotFiles"]
USER $APP_UID

ENTRYPOINT ["dotnet", "BeanBot.dll"]
