# syntax=docker/dockerfile:1
#
# The BiblioCommons circulation plugin. Python lives here and nowhere else — Bindery's own
# image stays a clean `aspnet` base.
#
# Stateless by design: signed-in library sessions are kept in memory only, never on disk, so
# there is no volume to mount and the root filesystem can be read-only. A restart costs each
# account one fresh sign-in, which is the right price for never holding a library session at
# rest.

FROM python:3.12-slim AS base

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PIP_NO_CACHE_DIR=1 \
    PIP_DISABLE_PIP_VERSION_CHECK=1

FROM base AS deps

WORKDIR /wheels
COPY requirements.txt .
RUN --mount=type=cache,target=/root/.cache/pip \
    pip wheel --wheel-dir /wheels -r requirements.txt

FROM base AS runtime

LABEL org.opencontainers.image.title="Bindery plugin: BiblioCommons" \
      org.opencontainers.image.description="Library search, holds, and loans for BiblioCommons catalogs (Seattle Public Library, KCLS, and others) over the Bindery plugin protocol." \
      org.opencontainers.image.source="https://github.com/OWNER/bindery" \
      org.opencontainers.image.licenses="Apache-2.0"

RUN --mount=type=bind,from=deps,source=/wheels,target=/wheels \
    pip install --no-index --find-links=/wheels /wheels/*.whl

RUN useradd --system --no-create-home --uid 10001 bindery

WORKDIR /app
COPY --chown=bindery:bindery app ./app

USER bindery

ENV BINDERY_PLUGIN_PORT=8080

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD python -c "import sys,urllib.request; sys.exit(0 if urllib.request.urlopen('http://127.0.0.1:8080/healthz', timeout=4).status == 200 else 1)"

# One worker: the session cache is per process, and a second worker would sign every account
# in twice. Scale with replicas as `mode: service` if ever needed — it will not be.
CMD ["sh", "-c", "exec uvicorn app.main:app --host 0.0.0.0 --port ${BINDERY_PLUGIN_PORT} --workers 1 --no-access-log"]
