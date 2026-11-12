FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore src/Loyalty.Api/Loyalty.Api.csproj
RUN dotnet publish src/Loyalty.Api/Loyalty.Api.csproj --no-restore -c Release -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:5105
EXPOSE 5105
COPY --from=build /out .
RUN mkdir -p /app/appdata && chown "$APP_UID" /app/appdata
USER $APP_UID
ENTRYPOINT ["dotnet", "Loyalty.Api.dll"]
