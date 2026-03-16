FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ["BeanBot/BeanBot.csproj", "BeanBot/"]
RUN dotnet restore "BeanBot/BeanBot.csproj"

COPY . .
WORKDIR /src/BeanBot
RUN dotnet publish "BeanBot.csproj" -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/runtime:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish ./

ENTRYPOINT ["dotnet", "BeanBot.dll"]
