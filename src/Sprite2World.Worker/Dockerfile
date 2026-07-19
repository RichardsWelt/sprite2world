FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/Sprite2World.Domain/Sprite2World.Domain.csproj src/Sprite2World.Domain/
COPY src/Sprite2World.Contracts/Sprite2World.Contracts.csproj src/Sprite2World.Contracts/
COPY src/Sprite2World.Application/Sprite2World.Application.csproj src/Sprite2World.Application/
COPY src/Sprite2World.Infrastructure/Sprite2World.Infrastructure.csproj src/Sprite2World.Infrastructure/
COPY src/Sprite2World.Worker/Sprite2World.Worker.csproj src/Sprite2World.Worker/
RUN dotnet restore src/Sprite2World.Worker/Sprite2World.Worker.csproj
COPY . .
RUN dotnet publish src/Sprite2World.Worker/Sprite2World.Worker.csproj -c Release -o /app/publish --no-restore
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
USER root
RUN mkdir -p /app/data && chown -R $APP_UID /app
USER $APP_UID
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet","Sprite2World.Worker.dll"]
