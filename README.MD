# SBanken + YNAB

Proof of concept. Hacket sammen litt kode fra forskjellige tutorials.
Planen er å lage en liten applikasjon som kan snurres igang for å overføre transaksjoner fra SBanken til YNAB ved behov.

# Setup guide

* Edit `C:\Users\_yourName_\AppData\Roaming\Microsoft\UserSecrets\7cbdd1ba-79e7-4c54-aafa-cfe969843f93\secrets.json`

```
{
  "YNABSettings:AccessToken": "XXXXXX",
  "SbankenSettings:Secret": "XXXXXX",
  "SbankenSettings:CustomerId": "XXXXXX",
  "SbankenSettings:ClientId": "XXXXXX",
  "SbankenSettings:MainAccountId": "XXXXXX"
}
```

## Alternative 1: Visual Studio
* Open in visual studio and run application

## Alternative 2: Shell
* cd src\
* dotnet run


## Requirements

* .NET Core 3.1 SDK: https://www.microsoft.com/net/download/windows
