# syntax=docker/dockerfile:1

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy solution + project files first for layer-cached restore.
COPY Clean4ork.sln ./
COPY src/Clean4ork.Core/Clean4ork.Core.csproj      src/Clean4ork.Core/
COPY src/Clean4ork.Data/Clean4ork.Data.csproj      src/Clean4ork.Data/
COPY src/Clean4ork.Scraper/Clean4ork.Scraper.csproj src/Clean4ork.Scraper/
COPY src/Clean4ork.Web/Clean4ork.Web.csproj        src/Clean4ork.Web/
RUN dotnet restore src/Clean4ork.Web/Clean4ork.Web.csproj

# Copy the rest and publish the Web app.
COPY . .
RUN dotnet publish src/Clean4ork.Web/Clean4ork.Web.csproj \
    -c Release -o /app /p:UseAppHost=false

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app ./

# Railway sets PORT; Program.cs binds 0.0.0.0:$PORT when present.
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
ENTRYPOINT ["dotnet", "Clean4ork.Web.dll"]
