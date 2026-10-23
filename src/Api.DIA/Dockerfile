FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json ./
COPY src/Dia.Contracts/Dia.Contracts.csproj src/Dia.Contracts/
COPY src/Api.DIA/Api.DIA.csproj src/Api.DIA/
RUN dotnet restore src/Api.DIA/Api.DIA.csproj
COPY src/Dia.Contracts/ src/Dia.Contracts/
COPY src/Api.DIA/ src/Api.DIA/
RUN dotnet publish src/Api.DIA/Api.DIA.csproj -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Api.DIA.dll"]
