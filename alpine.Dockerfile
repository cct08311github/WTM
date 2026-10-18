# WalkingTec.Mvvm Vue3 demo image.
#
# Stage 1 builds the Vue3 SPA (ClientApp) with Vite. The .NET project does not
# build it on publish; the app serves the compiled SPA from wwwroot, so the
# output is copied there in the final stage.

FROM node:20-alpine AS spa
WORKDIR /spa
COPY demo/WalkingTec.Mvvm.Vue3Demo/ClientApp/package.json demo/WalkingTec.Mvvm.Vue3Demo/ClientApp/package-lock.json ./
RUN npm ci
COPY demo/WalkingTec.Mvvm.Vue3Demo/ClientApp/ ./
RUN npm run build


FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /app

COPY . .
RUN dotnet publish "./demo/WalkingTec.Mvvm.Vue3Demo/WalkingTec.Mvvm.Vue3Demo.csproj" -c Release -o /app/out


FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app

# The aspnet alpine image defaults to globalization-invariant mode, which Microsoft.Data.SqlClient
# rejects ("Globalization Invariant Mode is not supported"), and invariant mode also breaks the
# framework's culture-based localization. Install ICU and turn invariant mode off.
RUN apk add --no-cache icu-libs
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

COPY --from=build /app/out ./
COPY --from=spa /spa/dist/ ./wwwroot/

# Note: System.Drawing is no longer supported on Linux in .NET 6+.
# If image processing is needed, use SkiaSharp or ImageSharp instead.

# Runtime configuration required in Production (supply at `docker run`, never bake into the image):
#   JwtOptions__SecurityKey    random string of at least 32 characters; the app refuses to
#                              start in Production with the sample key (see JwtOption.EnsureSecureKey)
#   Connections__0__Value      real database connection string (the sample LocalDB string does not work on Linux)
#   Connections__0__DbType     sqlserver | mysql | pgsql | sqlite | oracle | dameng (matching the connection string)
ENV ASPNETCORE_URLS=http://+:80
ENV ASPNETCORE_ENVIRONMENT=Production
ENTRYPOINT ["dotnet", "WalkingTec.Mvvm.Vue3Demo.dll"]
