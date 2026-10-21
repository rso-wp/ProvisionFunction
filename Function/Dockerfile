# The function app in the Azure Functions host, for local docker compose. In Azure the code is published to a Function
# App (Flex Consumption) instead: func azure functionapp publish <app>.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["Function/Function.csproj", "Function/"]
RUN dotnet restore "./Function/Function.csproj"
COPY . .
WORKDIR "/src/Function"
RUN dotnet publish "./Function.csproj" -c $BUILD_CONFIGURATION -o /home/site/wwwroot /p:UseAppHost=false

FROM mcr.microsoft.com/azure-functions/dotnet-isolated:4-dotnet-isolated10.0 AS final
ENV AzureWebJobsScriptRoot=/home/site/wwwroot \
    AzureFunctionsJobHost__Logging__Console__IsEnabled=true
COPY --from=build /home/site/wwwroot /home/site/wwwroot
