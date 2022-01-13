![Bean Bot Version 1.2.0](https://img.shields.io/badge/Bean%20Bot%20Version-1.2.0-green?style=plastic) ![.NET Core Master and Deploy Checks](https://github.com/EternalLiquet/BeanBot/workflows/.NET%20Core%20Master%20and%20Deploy%20Checks/badge.svg?branch=master)
# Bean Bot
This is the code behind the discord bot for the Illinois Livers server

One of the the primary intentions of this bot is to provide an automated way to verify new members to give them student roles.  
The secondary intent of this bot is to provide a gacha style game based on cards on schoolido.lu's API.  
And finally, the final (current) intent of this bot is to provide more sophistication to meetup information and RSVPing. However, this is no longer relevant as Discord has released it's own implementation of this functionality.
  
Discord link to the Illinois Server: https://discord.gg/a9hbx9S

# Bean Bot Setup Instructions

## Necessary Installations
To run Bean Bot, you will need the dotnet core SDK version 2.2, you can find that here: https://dotnet.microsoft.com/download/dotnet-core/2.2  
You will also need the 3.0 version to build the unit tests, which can be found here: https://dotnet.microsoft.com/download/dotnet-core/3.0

## Setup Steps
First, you will need to create a bot on Discord. You can do that with the following guide: https://discord.foxbot.me/docs/guides/getting_started/first-bot.html  
Once you have a bot token, you can invite your bot to the server. 

Afterwards, run the program either by:

     Using Visual Studio and hitting the play button

or


    • Navigate to the folder containing the .sln in the base of the repo.  
    • Run command `dotnet build --configuration Debug`  
    • This will build the solution in Debug mode. Navigate to the BeanBot/bin/Debug/netcoreapp2.2 folder.   
    • Run command `dotnet BeanBot.dll`

Once the bot is up and running, it will prompt you to input some settings.  
Variables that will have to be setup: 

    botToken: <INSERTBOTTOKENHERE>,  
    ilServerId: <It's named this but it can be the serverId of whatever server you are setting up>,  
    hatoeteUrl: <Contact EternalLiquet>,  
    yoshimaruUrl: <Contact EternalLiquet>  


If you have configured your settings correctly, the bot should connect to your server, enjoy!
