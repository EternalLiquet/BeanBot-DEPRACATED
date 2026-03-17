FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["BeanBot/BeanBot.csproj", "BeanBot/"]
COPY ["Directory.Packages.props", "./"]
COPY ["global.json", "./"]
RUN dotnet restore "BeanBot/BeanBot.csproj"

COPY . .
WORKDIR /src/BeanBot
RUN dotnet publish "BeanBot.csproj" -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish ./

ENTRYPOINT ["dotnet", "BeanBot.dll"]
