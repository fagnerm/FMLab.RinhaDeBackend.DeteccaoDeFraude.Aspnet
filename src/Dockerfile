FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
RUN apt-get update && apt-get install -y --no-install-recommends \
    clang \
    zlib1g-dev \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /src

COPY ["FMLab.RinhaDeBackend.DeteccaoDeFraude.Api", "FMLab.RinhaDeBackend.DeteccaoDeFraude.Api"]

RUN dotnet restore "FMLab.RinhaDeBackend.DeteccaoDeFraude.Api/FMLab.RinhaDeBackend.DeteccaoDeFraude.Api.csproj" \
    -r linux-x64

COPY / /

RUN dotnet publish "FMLab.RinhaDeBackend.DeteccaoDeFraude.Api/FMLab.RinhaDeBackend.DeteccaoDeFraude.Api.csproj" \
    -c Release \
    -r linux-x64 \
    -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["./FMLab.RinhaDeBackend.DeteccaoDeFraude.Api"]
