FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY server/AicIv.Server.csproj server/
RUN dotnet restore server/AicIv.Server.csproj
COPY server/ server/
RUN dotnet publish server/AicIv.Server.csproj -c Release --no-restore -o /publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /publish/ .
ENV HOST=0.0.0.0
ENV PORT=10000
EXPOSE 10000
USER $APP_UID
ENTRYPOINT ["dotnet", "AicIv.Server.dll"]
