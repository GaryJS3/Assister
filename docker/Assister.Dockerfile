FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY src/Assister/Assister.csproj src/Assister/
COPY src/Assister.Contracts/Assister.Contracts.csproj src/Assister.Contracts/
RUN dotnet restore src/Assister/Assister.csproj
COPY src/ src/
RUN dotnet publish src/Assister/Assister.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
USER root
RUN mkdir /data && chown app:app /data
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080 Assister__DataPath=/data
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "Assister.dll"]
