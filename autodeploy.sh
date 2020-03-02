cd ../BeanBot
git pull origin master
dotnet build --configuration Release
cd bin/Release/netcoreapp2.2
dotnet BeanBot.dll
