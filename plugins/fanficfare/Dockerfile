# syntax=docker/dockerfile:1
#
# The FanFicFare plugin image. Every Python dependency in the system lives in here and
# nowhere else — Bindery's own image is a clean `aspnet` base with no interpreter in it.
# Updating FanFicFare, which you will do often, is a rebuild of this image and a tag bump
# in values.yaml.

FROM python:3.14-slim AS base

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PIP_NO_CACHE_DIR=1 \
    PIP_DISABLE_PIP_VERSION_CHECK=1

# ---------------------------------------------------------------- dependencies

FROM base AS deps

WORKDIR /wheels
COPY requirements.txt .
RUN --mount=type=cache,target=/root/.cache/pip \
    pip wheel --wheel-dir /wheels -r requirements.txt

# ---------------------------------------------------------------- runtime

FROM base AS runtime

LABEL org.opencontainers.image.title="Bindery plugin: FanFicFare" \
      org.opencontainers.image.description="Downloads fanfiction via FanFicFare and serves it over the Bindery plugin protocol." \
      org.opencontainers.image.source="https://github.com/OWNER/bindery" \
      org.opencontainers.image.licenses="Apache-2.0"

RUN --mount=type=bind,from=deps,source=/wheels,target=/wheels \
    pip install --no-index --find-links=/wheels /wheels/*.whl

# Runs unprivileged with a read-only root filesystem; /work is the only writable path and
# it holds nothing but in-flight job artifacts.
RUN useradd --system --create-home --uid 10001 bindery \
    && mkdir -p /work \
    && chown -R bindery:bindery /work

WORKDIR /app
COPY --chown=bindery:bindery app ./app

USER bindery

ENV BINDERY_PLUGIN_PORT=8080 \
    BINDERY_PLUGIN_WORKDIR=/work \
    HOME=/work

EXPOSE 8080
VOLUME ["/work"]

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD python -c "import sys,urllib.request; sys.exit(0 if urllib.request.urlopen('http://127.0.0.1:8080/healthz', timeout=4).status == 200 else 1)"

# One worker on purpose: job state and artifacts live in this process's memory and its
# scratch directory. Scale by running more replicas as `mode: service`, not more workers.
CMD ["sh", "-c", "exec uvicorn app.main:app --host 0.0.0.0 --port ${BINDERY_PLUGIN_PORT} --workers 1 --no-access-log"]
