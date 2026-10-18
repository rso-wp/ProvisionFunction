# Build context: the ProvisionFunction folder (the solution directory), like the other services.
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["Function/Function.csproj", "Function/"]
RUN dotnet restore "./Function/Function.csproj"
COPY . .
WORKDIR "/src/Function"
RUN dotnet publish "./Function.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# Runs as root: it talks to the docker socket mounted from the host.
FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Function.dll"]
