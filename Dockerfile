# See https://aka.ms/containerfastmode to understand how Visual Studio uses this Dockerfile to build your images for faster debugging.
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY ["Agent Status/Agent Status.csproj", "Agent Status/"]
RUN dotnet restore "Agent Status/Agent Status.csproj"
COPY . .
WORKDIR "/src/Agent Status"
RUN dotnet build "Agent Status.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "Agent Status.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENV RUNNING_IN_DOCKER=true
ENTRYPOINT ["dotnet", "Agent Status.dll"]
