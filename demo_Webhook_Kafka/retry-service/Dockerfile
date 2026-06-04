FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY ["retry-service/retry-service.csproj", "retry-service/"]
RUN dotnet restore "retry-service/retry-service.csproj"

COPY . .
WORKDIR "/src/retry-service"
RUN dotnet publish "retry-service.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

EXPOSE 3003
ENV ASPNETCORE_URLS=http://+:3003

ENTRYPOINT ["dotnet", "retry-service.dll"]