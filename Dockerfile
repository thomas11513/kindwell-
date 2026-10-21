FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Kindwell.csproj ./
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_ENVIRONMENT=Production \
    DATA_DIR=/data \
    ASPNETCORE_URLS=http://0.0.0.0:10000
# The persistent disk is mounted at /data: it holds the database and uploaded images.
CMD ["sh", "-c", "mkdir -p /data/uploads && rm -rf wwwroot/uploads && ln -s /data/uploads wwwroot/uploads && dotnet Kindwell.dll"]
