FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# O restore fica em camada propria: so os .csproj entram antes do codigo, entao mudar um .cs nao refaz o download
# dos pacotes. Todos os projetos de src/ precisam estar aqui, inclusive os Contracts.
COPY nuget.config ./
COPY src/OficinaMecanica.Contracts/OficinaMecanica.Contracts.csproj src/OficinaMecanica.Contracts/
COPY src/OficinaMecanica.Billing.Domain/OficinaMecanica.Billing.Domain.csproj src/OficinaMecanica.Billing.Domain/
COPY src/OficinaMecanica.Billing.Application/OficinaMecanica.Billing.Application.csproj src/OficinaMecanica.Billing.Application/
COPY src/OficinaMecanica.Billing.Infrastructure/OficinaMecanica.Billing.Infrastructure.csproj src/OficinaMecanica.Billing.Infrastructure/
COPY src/OficinaMecanica.Billing.API/OficinaMecanica.Billing.API.csproj src/OficinaMecanica.Billing.API/

RUN dotnet restore src/OficinaMecanica.Billing.API/OficinaMecanica.Billing.API.csproj

COPY src/ src/
RUN dotnet publish src/OficinaMecanica.Billing.API/OficinaMecanica.Billing.API.csproj -c Release -o /app/publish --no-restore

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
# A porta 8080 vem do ASPNETCORE_HTTP_PORTS da imagem base (aspnet:8.0); redefinir ASPNETCORE_URLS aqui geraria o aviso
# "Overriding HTTP_PORTS" a cada boot.
# Usuario nao-root: a imagem base ja traz o usuario app (UID em $APP_UID).
USER $APP_UID
ENTRYPOINT ["dotnet", "OficinaMecanica.Billing.API.dll"]
