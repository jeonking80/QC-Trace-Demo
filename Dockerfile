# 빌드 단계
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY QcTrace.sln ./
COPY src/QcTrace.Web/QcTrace.Web.csproj src/QcTrace.Web/
RUN dotnet restore src/QcTrace.Web/QcTrace.Web.csproj
COPY src/ src/
RUN dotnet publish src/QcTrace.Web/QcTrace.Web.csproj -c Release -o /app/publish --no-restore

# 실행 단계
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
COPY --from=build /app/publish .
USER app
ENTRYPOINT ["dotnet", "QcTrace.Web.dll"]
