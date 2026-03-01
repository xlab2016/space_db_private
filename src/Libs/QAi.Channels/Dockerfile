#See https://aka.ms/containerfastmode to understand how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base

WORKDIR /app
EXPOSE 8080
EXPOSE 443

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["QAi.Channels/QAi.Channels.csproj", "QAi.Channels/"]
RUN dotnet restore "QAi.Channels/QAi.Channels.csproj"
COPY . .
WORKDIR "/src/QAi.Channels"
RUN dotnet build "QAi.Channels.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "QAi.Channels.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "QAi.Channels.dll"]