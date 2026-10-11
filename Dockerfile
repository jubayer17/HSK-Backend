# Function App image: .NET 8 isolated worker + Java 17 + the OpenDataLoader JAR in one container (plan 4 / 9.2).
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Directory.Build.targets ./
COPY migrations ./migrations
COPY src/HousekeepingParser ./src/HousekeepingParser
RUN dotnet publish src/HousekeepingParser/HousekeepingParser.csproj -c Release -o /home/site/wwwroot

FROM mcr.microsoft.com/azure-functions/dotnet-isolated:4-dotnet-isolated8.0
RUN apt-get update \
 && apt-get install -y --no-install-recommends openjdk-17-jre-headless \
 && rm -rf /var/lib/apt/lists/*
# Fetched by tools/get-opendataloader.sh before the build (CI does this step).
COPY tools/opendataloader/opendataloader-pdf.jar /opt/opendataloader/opendataloader-pdf.jar
COPY --from=build /home/site/wwwroot /home/site/wwwroot
ENV AzureWebJobsScriptRoot=/home/site/wwwroot \
    AzureFunctionsJobHost__Logging__Console__IsEnabled=true \
    WEBSITE_TIME_ZONE=Europe/Berlin \
    TZ=Europe/Berlin \
    OpenDataLoader__JavaExecutable=/usr/bin/java \
    OpenDataLoader__JarPath=/opt/opendataloader/opendataloader-pdf.jar
