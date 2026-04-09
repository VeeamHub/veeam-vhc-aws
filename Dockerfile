FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/VeeamVhcAws/VeeamVhcAws.csproj src/VeeamVhcAws/
RUN dotnet restore src/VeeamVhcAws/VeeamVhcAws.csproj -r linux-x64
COPY src/ src/
RUN dotnet publish src/VeeamVhcAws/VeeamVhcAws.csproj \
    -c Release \
    -r linux-x64 \
    --self-contained \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -o /app/publish \
    --nologo

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0
WORKDIR /app
COPY --from=build /app/publish/veeam-vhc-aws .
ENTRYPOINT ["./veeam-vhc-aws"]
CMD ["all", "--config", "/config/config.yaml"]
