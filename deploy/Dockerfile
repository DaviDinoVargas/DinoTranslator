# API da versão Plus. Construída a partir da raiz do repositório:
#   docker build -f deploy/Dockerfile -t dinotranslator-api .
# Imagem final "chiseled": só o runtime do .NET, sem shell nem gerenciador de pacotes, rodando
# com usuário sem privilégios.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/DinoTranslator.Shared/DinoTranslator.Shared.csproj src/DinoTranslator.Shared/
COPY src/DinoTranslator.Server/DinoTranslator.Server.csproj src/DinoTranslator.Server/
RUN dotnet restore src/DinoTranslator.Server/DinoTranslator.Server.csproj
COPY src/DinoTranslator.Shared/ src/DinoTranslator.Shared/
COPY src/DinoTranslator.Server/ src/DinoTranslator.Server/
RUN dotnet publish src/DinoTranslator.Server/DinoTranslator.Server.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_gcServer=1
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "DinoTranslator.Server.dll"]
