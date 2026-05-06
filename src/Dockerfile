FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY ["Directory.Packages.props", "."]
COPY ["FMLab.RinhaDeBackend.DeteccaoDeFraude.Api/FMLab.RinhaDeBackend.DeteccaoDeFraude.Api.csproj", "FMLab.RinhaDeBackend.DeteccaoDeFraude.Api/"]
RUN dotnet restore "FMLab.RinhaDeBackend.DeteccaoDeFraude.Api/FMLab.RinhaDeBackend.DeteccaoDeFraude.Api.csproj"

COPY . .
RUN dotnet publish "FMLab.RinhaDeBackend.DeteccaoDeFraude.Api/FMLab.RinhaDeBackend.DeteccaoDeFraude.Api.csproj" \
    -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "FMLab.RinhaDeBackend.DeteccaoDeFraude.Api.dll"]
