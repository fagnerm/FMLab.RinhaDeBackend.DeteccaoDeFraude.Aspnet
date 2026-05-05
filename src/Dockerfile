FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY ["FMLab.RinhaDeBackend.DeteccaoDeFraude.Api", "FMLab.RinhaDeBackend.DeteccaoDeFraude.Api"]
COPY ["/Directory.Packages.props", "/"]

RUN dotnet restore "FMLab.RinhaDeBackend.DeteccaoDeFraude.Api/FMLab.RinhaDeBackend.DeteccaoDeFraude.Api.csproj"

COPY / /
WORKDIR "/FMLab.RinhaDeBackend.DeteccaoDeFraude.Api"
RUN dotnet build "FMLab.RinhaDeBackend.DeteccaoDeFraude.Api.csproj" -c $BUILD_CONFIGURATION -o /app/build

 FROM build AS publish
 ARG BUILD_CONFIGURATION=Release
 RUN dotnet publish "/FMLab.RinhaDeBackend.DeteccaoDeFraude.Api/FMLab.RinhaDeBackend.DeteccaoDeFraude.Api.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

 FROM base AS final
 WORKDIR /app
 COPY --from=publish /app/publish .
 ENTRYPOINT ["dotnet", "FMLab.RinhaDeBackend.DeteccaoDeFraude.Api.dll"]