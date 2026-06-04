FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY ["core-service/core-service.csproj", "core-service/"]
RUN dotnet restore "core-service/core-service.csproj"

COPY . .
WORKDIR "/src/core-service"
RUN dotnet publish "core-service.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

EXPOSE 3002
ENV ASPNETCORE_HTTP_PORTS=3002

ENTRYPOINT ["dotnet", "core-service.dll"]