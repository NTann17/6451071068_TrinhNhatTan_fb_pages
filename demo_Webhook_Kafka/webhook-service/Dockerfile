FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY ["webhook-service/webhook-service.csproj", "webhook-service/"]
RUN dotnet restore "webhook-service/webhook-service.csproj"

COPY . .
WORKDIR "/src/webhook-service"
RUN dotnet publish "webhook-service.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

EXPOSE 3001
ENV ASPNETCORE_URLS=http://+:3001

ENTRYPOINT ["dotnet", "webhook-service.dll"]