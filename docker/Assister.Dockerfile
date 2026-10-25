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
COPY tools/Assister.VoiceBench/EndpointBenchmark.cs tools/Assister.VoiceBench/
RUN dotnet publish tools/Assister.ServiceProbe/Assister.ServiceProbe.csproj -c Release -o /probe

FROM mcr.microsoft.com/dotnet/aspnet:10.0
USER root
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg && rm -rf /var/lib/apt/lists/*
RUN mkdir /data && chown app:app /data
WORKDIR /app
ADD --chmod=0444 --checksum=sha256:2bb026316b14a660486a75b1733cd3fbab8c2fd0314dc9af7be49f8cca967e4f https://huggingface.co/pipecat-ai/smart-turn-v3/resolve/f766f81d3cfdf7737ac64aad813d91bbfd56bf93/smart-turn-v3.2-cpu.onnx /app/models/smart-turn-v3.2-cpu.onnx
COPY licenses/SMART-TURN.txt /app/models/LICENSE.txt
COPY --from=build /app .
COPY --from=build /probe /probe
ENV ASPNETCORE_HTTP_PORTS=8080 Assister__DataPath=/data
USER app
EXPOSE 8080 8082
ENTRYPOINT ["dotnet", "Assister.dll"]
