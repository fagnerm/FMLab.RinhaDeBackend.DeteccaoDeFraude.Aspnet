FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
RUN apk add --no-cache \
    clang \
    build-base \
    zlib-dev
WORKDIR /src

COPY ["FMLab.RinhaDeBackend.DeteccaoDeFraude.Api", "FMLab.RinhaDeBackend.DeteccaoDeFraude.Api"]

RUN dotnet restore "FMLab.RinhaDeBackend.DeteccaoDeFraude.Api/FMLab.RinhaDeBackend.DeteccaoDeFraude.Api.csproj" \
    -r linux-musl-x64

COPY / /

RUN dotnet publish "FMLab.RinhaDeBackend.DeteccaoDeFraude.Api/FMLab.RinhaDeBackend.DeteccaoDeFraude.Api.csproj" \
    -c Release \
    -r linux-musl-x64 \
    -o /app/publish

# Se references.json.gz estiver presente no build context, reconstrói o índice
# dentro do container Alpine garantindo que o índice foi gerado na mesma plataforma
# (musl + AVX2) que vai executar o servidor.
# No GitHub Actions (sem o .gz), usa o references.idx pré-construído do repositório.
RUN SRC="/src/FMLab.RinhaDeBackend.DeteccaoDeFraude.Api/App_Data/references.json.gz" && \
    DST="/app/publish/App_Data" && \
    if [ -f "$SRC" ]; then \
        echo "Reconstruindo references.idx no Alpine..." && \
        cp "$SRC" "$DST/" && \
        /app/publish/FMLab.RinhaDeBackend.DeteccaoDeFraude.Api --build-index && \
        rm "$DST/references.json.gz"; \
    else \
        echo "references.json.gz ausente — usando references.idx pré-construído"; \
    fi

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-alpine AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["./FMLab.RinhaDeBackend.DeteccaoDeFraude.Api"]
