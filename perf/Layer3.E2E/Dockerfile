# Overlays a build of this repo's server code onto the upstream jellyfin/jellyfin image, so
# jellyfin-web (never vendored in this repo) and the native runtime/deps come for free. Build
# context is expected to be a repo checkout (a disposable git worktree for ref-switching — see
# ../run-layer3.sh), not this directory.
FROM docker.io/jellyfin/jellyfin:preview AS base

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish Jellyfin.Server/Jellyfin.Server.csproj \
    -c Release \
    -o /out \
    --no-self-contained \
    -p:DebugType=none \
    -p:DebugSymbols=false

# The base image's deps.json is keyed by a self-contained runtime target (e.g.
# ".NETCoreApp,Version=v10.0/linux-x64"); our framework-dependent build produces a differently
# keyed one. Overlay our build's target/library entries onto base's so the base's native host can
# still resolve everything, including any package base didn't ship.
COPY --from=base /jellyfin/jellyfin.deps.json /base-deps.json
COPY perf/Layer3.E2E/merge_deps.py /merge_deps.py
RUN apt-get update -qq && apt-get install -y --no-install-recommends python3 && rm -rf /var/lib/apt/lists/* \
    && python3 /merge_deps.py

FROM base
# Replace every managed DLL our build produced (not just Jellyfin.*/Emby.*/MediaBrowser.* — also
# their NuGet dependencies, e.g. Microsoft.EntityFrameworkCore.dll, Microsoft.Data.Sqlite.dll:
# base's own deps.json already lists these, and the merged deps.json below still points at them,
# so leaving base's copies in place while overlaying a possibly different version in the deps.json
# metadata alone throws FileNotFoundException at startup — confirmed empirically). Leave the .NET
# runtime, native .so files, runtimeconfig.json (base's is self-contained/pinned; our build's own
# is framework-dependent and NOT interchangeable with it), and jellyfin-web untouched from
# upstream.
COPY --from=build /out/*.dll /jellyfin/
COPY --from=build /out/jellyfin.merged.deps.json /jellyfin/jellyfin.deps.json
