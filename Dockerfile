FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /app

COPY . .
RUN dotnet publish "./demo/WalkingTec.Mvvm.Demo/WalkingTec.Mvvm.Demo.csproj" -c Release -o /app/out


FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/out ./

ENV ASPNETCORE_URLS=http://+:80
ENV ASPNETCORE_ENVIRONMENT=Production

# #923/#931 (P0 security fix): the demo's appsettings.json no longer ships a
# JwtOptions:SecurityKey value (the old value -- "super" -- was a publicly known
# placeholder anyone reading this repo, including its public GitHub mirror, could
# use to forge an access token). With ASPNETCORE_ENVIRONMENT=Production above, this
# container will now refuse to start (throws InvalidOperationException from
# AddWtmAuthentication) unless a real key is supplied -- there is no Development
# carve-out at this environment setting, and none was added here: silently
# downgrading this image's own default to Development so it boots unattended would
# generate a throwaway per-process key instead, hiding the requirement rather than
# surfacing it, and would invalidate every access token on every container restart.
#
# WTM only ever reads the JwtOptions__SecurityKey environment variable itself (the
# double underscore is ASP.NET Core configuration's standard section-separator
# convention, equivalent to appsettings.json's "JwtOptions": { "SecurityKey": "..."
# } -- there is no built-in "_FILE" / secret-path indirection), so however the key
# reaches that variable is up to your deployment platform:
#
#   1. Plain env var, generated once and stored in your own secret manager:
#        docker run -e JwtOptions__SecurityKey="$(openssl rand -base64 32)" -p 80:80 <image>
#
#   2. Docker secret, exported into the env var by a tiny entrypoint wrapper (a
#      Docker secret is mounted as a file under /run/secrets/<name>, not injected
#      as an env var automatically):
#        docker secret create jwt_security_key -   # reads the key from stdin
#        # entrypoint.sh (replace this Dockerfile's ENTRYPOINT with it):
#        #   export JwtOptions__SecurityKey="$(cat /run/secrets/jwt_security_key)"
#        #   exec dotnet WalkingTec.Mvvm.Demo.dll
#        docker service create --secret jwt_security_key --entrypoint /app/entrypoint.sh <image>
#
#   3. Kubernetes Secret, projected as an env var directly (no wrapper needed):
#        kubectl create secret generic wtm-jwt --from-literal=SecurityKey="$(openssl rand -base64 32)"
#        # container spec: env: [{name: JwtOptions__SecurityKey, valueFrom: {secretKeyRef: {name: wtm-jwt, key: SecurityKey}}}]
#
# See docs/production-readiness.md's #923/#931 entry for the full design and
# docs/wtm-developer-manual.md Section 17.1 for the appsettings.json / user-secrets
# equivalents used in non-container deployments.
ENTRYPOINT ["dotnet", "WalkingTec.Mvvm.Demo.dll"]
