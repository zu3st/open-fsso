FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:0d99ebf1ccad15037b5c98c9fe8979d6b8dbac23812feae2277a0b277b5c5ffa AS workspace
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
WORKDIR /src
SHELL ["/bin/bash", "-o", "pipefail", "-c"]
COPY Directory.Build.props .editorconfig OpenFsso.slnx global.json ./
COPY src/ ./src/
COPY tools/ ./tools/
COPY tests/ ./tests/
COPY config/*.example.yaml ./config/
COPY config/*.schema.json ./config/
RUN --mount=type=cache,target=/root/.nuget/packages dotnet restore OpenFsso.slnx

FROM workspace AS format-apply
RUN --mount=type=cache,target=/root/.nuget/packages dotnet format OpenFsso.slnx --no-restore --severity warn --report /format-report.json && dotnet format whitespace OpenFsso.slnx --no-restore && dotnet format OpenFsso.slnx --no-restore --severity warn --verify-no-changes
RUN mkdir /formatted && find src tools tests -type f -name '*.cs' ! -path '*/obj/*' ! -path '*/bin/*' -exec cp --parents '{}' /formatted/ \; && cp /format-report.json /formatted/
FROM scratch AS formatted
COPY --from=format-apply /formatted/ /

FROM workspace AS format-check
RUN --mount=type=cache,target=/root/.nuget/packages dotnet format OpenFsso.slnx --no-restore --severity warn --verify-no-changes

FROM format-check AS build
RUN --mount=type=cache,target=/root/.nuget/packages mkdir -p /out/test-results && dotnet test --project tests/OpenFsso.Tests -c Release --report-trx --report-trx-filename results.trx --results-directory /out/test-results
RUN --mount=type=cache,target=/root/.nuget/packages dotnet publish src/OpenFsso.Receiver -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o /out/receiver
RUN --mount=type=cache,target=/root/.nuget/packages dotnet publish src/OpenFsso.Client -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o /out/client
RUN --mount=type=cache,target=/root/.nuget/packages dotnet publish src/OpenFsso.Tray -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o /out/client
FROM scratch AS artifacts
COPY --from=build /out/ /
