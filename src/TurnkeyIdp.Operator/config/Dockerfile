FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /operator

COPY ./ ./
RUN dotnet publish -c Release /p:AssemblyName=operator -o out

# The runner for the application
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final

RUN groupadd k8s-operator && useradd -g k8s-operator operator-user

WORKDIR /operator
COPY --chown=operator-user:k8s-operator --from=build /operator/out/ ./

USER operator-user

ENTRYPOINT [ "dotnet", "operator.dll" ]