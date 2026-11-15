# syntax=docker/dockerfile:1
#
# The WordPress serials plugin. Python lives here and nowhere else — Bindery's own image stays
# a clean `aspnet` base with no interpreter.
#
# /work holds per-job scratch (jobs/) and a chapter cache (data/). The cache is an
# optimisation, never state that matters: without a persistent volume every update simply
# re-reads every chapter. Mount one to make updates of long serials cheap.

FROM python:3.12-slim AS base

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

LABEL org.opencontainers.image.title="Bindery plugin: WordPress serials" \
      org.opencontainers.image.description="Binds web serials published on WordPress sites into EPUBs, over the Bindery plugin protocol." \
      org.opencontainers.image.source="https://github.com/OWNER/bindery" \
      org.opencontainers.image.licenses="Apache-2.0"

RUN --mount=type=bind,from=deps,source=/wheels,target=/wheels \
    pip install --no-index --find-links=/wheels /wheels/*.whl

# Runs unprivileged. /work is the only writable path.
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

# One worker: jobs and their artifacts are tracked in this process's memory. Scale with
# replicas as `mode: service`, not with more workers.
CMD ["sh", "-c", "exec uvicorn app.main:app --host 0.0.0.0 --port ${BINDERY_PLUGIN_PORT} --workers 1 --no-access-log"]
