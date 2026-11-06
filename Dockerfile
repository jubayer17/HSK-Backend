# Final Phase 1 decision: all PDF parsing runs inside .NET; the runtime no longer needs Java or a JAR.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Directory.Build.targets ./
COPY migrations ./migrations
COPY src/HousekeepingParser ./src/HousekeepingParser
RUN dotnet publish src/HousekeepingParser/HousekeepingParser.csproj -c Release -o /home/site/wwwroot

FROM mcr.microsoft.com/azure-functions/dotnet-isolated:4-dotnet-isolated8.0
COPY --from=build /home/site/wwwroot /home/site/wwwroot
ENV AzureWebJobsScriptRoot=/home/site/wwwroot \
    AzureFunctionsJobHost__Logging__Console__IsEnabled=true \
    WEBSITE_TIME_ZONE=Europe/Berlin \
    TZ=Europe/Berlin
