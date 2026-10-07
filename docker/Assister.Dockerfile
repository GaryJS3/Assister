FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY src/Assister/Assister.csproj src/Assister/
COPY src/Assister.Contracts/Assister.Contracts.csproj src/Assister.Contracts/
RUN dotnet restore src/Assister/Assister.csproj
COPY src/ src/
COPY bridge/satellite.proto bridge/
COPY tones/ tones/
RUN dotnet publish src/Assister/Assister.csproj -c Release -o /app --no-restore
COPY tools/Assister.ServiceProbe/ tools/Assister.ServiceProbe/
RUN dotnet publish tools/Assister.ServiceProbe/Assister.ServiceProbe.csproj -c Release -o /probe

FROM mcr.microsoft.com/dotnet/aspnet:10.0
USER root
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg && rm -rf /var/lib/apt/lists/*
RUN mkdir /data && chown app:app /data
WORKDIR /app
COPY --from=build /app .
COPY --from=build /probe /probe
ENV ASPNETCORE_HTTP_PORTS=8080 Assister__DataPath=/data
USER app
EXPOSE 8080 8082
ENTRYPOINT ["dotnet", "Assister.dll"]
