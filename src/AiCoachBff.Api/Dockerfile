# Contexto do build é a RAIZ do repo BFF (az acr build --file src/AiCoachBff.Api/Dockerfile .)
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["AiCoachBff.sln", "./"]
COPY ["src/AiCoachBff.Api/AiCoachBff.Api.csproj", "src/AiCoachBff.Api/"]
COPY ["tests/AiCoachBff.Tests/AiCoachBff.Tests.csproj", "tests/AiCoachBff.Tests/"]
RUN dotnet restore AiCoachBff.sln
COPY . .
WORKDIR /src/src/AiCoachBff.Api
RUN dotnet publish -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
# Porta do Container App (o ingress mira 8080, molde CheckFine)
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
COPY --from=build /app/publish .
USER $APP_UID
