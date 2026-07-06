FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore first so layers cache when only source changes.
COPY src/Qwiik.Invoicing.Api/Qwiik.Invoicing.Api.csproj src/Qwiik.Invoicing.Api/
RUN dotnet restore src/Qwiik.Invoicing.Api/Qwiik.Invoicing.Api.csproj

COPY src/ src/
RUN dotnet publish src/Qwiik.Invoicing.Api/Qwiik.Invoicing.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
# Non-root user provided by the base image
USER app
ENTRYPOINT ["dotnet", "Qwiik.Invoicing.Api.dll"]
