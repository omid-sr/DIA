FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json ./
COPY src/Dia.Contracts/Dia.Contracts.csproj src/Dia.Contracts/
COPY src/Producer/Producer.csproj src/Producer/
RUN dotnet restore src/Producer/Producer.csproj
COPY src/Dia.Contracts/ src/Dia.Contracts/
COPY src/Producer/ src/Producer/
RUN dotnet publish src/Producer/Producer.csproj -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "Producer.dll"]
