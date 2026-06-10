ARG baseimage
FROM $baseimage

ENV CORECLR_ENABLE_PROFILING=1
ENV CORECLR_PROFILER="{3B1DAA64-89D4-4999-ABF4-6A979B650B7D}"
ENV CORECLR_PROFILER_PATH_32=/sealights/libSL.DotNet.ProfilerLib.Linux.so
ENV CORECLR_PROFILER_PATH_64=/sealights/libSL.DotNet.ProfilerLib.Linux.so
ENV SL_PROFILER_INITIALIZECOLLECTOR=1
ENV SL_PROFILER_INITIALIZECOLLECTOR_MODE="cdAgent"
ENV SL_PROFILER_BLOCKING_CONNECTION_STARTUP="ASYNC"
ENV SL_FEATURES_IDENTIFYMETHODSBYFQN="true"
# SL_SESSION_TOKEN is injected at runtime (OpenShift env, sourced from a Secret).
# Do NOT hardcode the token here — this file is committed to git.

ARG BUILD_NUMBER
ENV SL_GENERAL_APPNAME="dse_searchapi"
ENV SL_GENERAL_BRANCHNAME="main"
ENV SL_GENERAL_BUILDNAME=$BUILD_NUMBER
# TODO: replace "my_lab" with the real lab ID from the SeaLights dashboard (or drop this line)
ENV SL_LABID="my_lab"
ENV SL_SCAN_BINDIR="/app"
ENV SL_SCAN_INCLUDENAMESPACES_0="Dse.*"
ENV SL_SCAN_INCLUDEASSEMBLIES="Dse.*"

USER root

# Install the SeaLights .NET Core agent (glibc/RHEL 8 build) into /sealights.
# Provides libSL.DotNet.ProfilerLib.Linux.so referenced by the CORECLR_* paths above.
#
# Proxy: the ADO template passes no proxy build-arg, so it defaults to PNC's egress proxy (the same one
# this pipeline's Sysdig step uses); HTTPS_PROXY/AGENT_PROXYURL still override it via --build-arg.
# -4: agents.sealights.co resolves IPv6-only and the agent has no IPv6 egress (the proxy is reached over IPv4).
# --insecure/--proxy-insecure: the proxy intercepts TLS with a CA the agent doesn't trust and has no cert
# store (or openssl) to add it to — internal download over the trusted VPN. Prefer `--cacert <file>` if a
# corporate CA ever becomes available.
ARG HTTPS_PROXY
ARG AGENT_PROXYURL
RUN mkdir -p /sealights/logs && \
    proxy="${HTTPS_PROXY:-${AGENT_PROXYURL:-http://vz-proxy.pncint.net:8080}}" && \
    curl -4 -fsSL --insecure --proxy-insecure ${proxy:+--proxy "$proxy"} -o /tmp/sl-agent.tar.gz \
      https://agents.sealights.co/dotnetcore/latest/sealights-dotnet-agent-linux-self-contained.tar.gz && \
    tar -xzf /tmp/sl-agent.tar.gz --directory /sealights && \
    rm /tmp/sl-agent.tar.gz

# Higher-level port mapped to port 80/443 in OpenShift
EXPOSE 8080
ENV ASPNETCORE_URLS=http://*:8080
ENV ASPNETCORE_HTTP_PORTS=8080

# Flush stdout/stderr line-by-line so startup crashes actually surface in `oc logs`.
ENV DOTNET_CONSOLE_DISABLE_BUFFERING=1
ENV DOTNET_RUNNING_IN_CONTAINER=true

WORKDIR /app
ENV PATH=/app:${PATH} HOME=/app

COPY uid_entrypoint .
COPY app .

RUN chmod -R u+x /app && \
    chgrp -R 0 /app /sealights && \
    chmod -R g=u /app /sealights /etc/passwd

USER 10001

# uid_entrypoint resolves the (random) OpenShift UID into /etc/passwd, then exec's
# the dotnet process. Single ENTRYPOINT — having two silently drops the first.
ENTRYPOINT ["uid_entrypoint", "dotnet", "Dse.Runtime.dll"]
