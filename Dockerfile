FROM mcr.microsoft.com/dotnet/sdk:10.0-noble@sha256:35d40304542c8689331f8cab17c65926cdf48fe711e289321d71924b230a7d29 AS build
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

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra@sha256:00e0ad6a7ef8c0c1391b87f05c7ac757a15740455688f2bfcd146a3f4b987efd AS final
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
