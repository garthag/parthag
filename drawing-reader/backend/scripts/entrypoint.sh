#!/usr/bin/env bash
# Container entrypoint: wait for the database, migrate, optionally seed, then run the requested role.
set -euo pipefail

wait_for_db() {
  python - <<'PY'
import sys, time
from sqlalchemy import create_engine, text
from app.core.config import settings
engine = create_engine(settings.DATABASE_URL, pool_pre_ping=True)
for attempt in range(60):
    try:
        with engine.connect() as conn:
            conn.execute(text("SELECT 1"))
        sys.exit(0)
    except Exception as exc:  # noqa: BLE001
        print(f"waiting for database ({attempt + 1}/60): {exc.__class__.__name__}", flush=True)
        time.sleep(2)
sys.exit("database not reachable")
PY
}

role="${1:-api}"
case "$role" in
  api)
    wait_for_db
    alembic upgrade head
    if [[ "${SEED_ON_START:-false}" == "true" ]]; then
      python -m app.seed.seed
    fi
    exec gunicorn app.main:app \
      --worker-class uvicorn.workers.UvicornWorker \
      --workers "${WEB_CONCURRENCY:-4}" \
      --bind "0.0.0.0:${PORT:-8000}" \
      --timeout "${GUNICORN_TIMEOUT:-300}" \
      --access-logfile - --forwarded-allow-ips="*"
    ;;
  dev)
    wait_for_db
    alembic upgrade head
    if [[ "${SEED_ON_START:-false}" == "true" ]]; then
      python -m app.seed.seed
    fi
    exec uvicorn app.main:app --host 0.0.0.0 --port "${PORT:-8000}" --reload
    ;;
  worker)
    wait_for_db
    exec celery -A app.workers.celery_app worker --loglevel="${LOG_LEVEL:-INFO}" -Q ocr \
      --concurrency="${CELERY_CONCURRENCY:-2}"
    ;;
  migrate)
    wait_for_db
    exec alembic upgrade head
    ;;
  seed)
    wait_for_db
    alembic upgrade head
    exec python -m app.seed.seed "${@:2}"
    ;;
  *)
    exec "$@"
    ;;
esac
