FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY ["backend-api/backend-api.csproj", "backend-api/"]
RUN dotnet restore "backend-api/backend-api.csproj"

COPY . .
WORKDIR "/src/backend-api"
RUN dotnet publish "backend-api.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

EXPOSE 3000
ENV ASPNETCORE_URLS=http://+:3000

ENTRYPOINT ["dotnet", "backend-api.dll"]