"""Export the OpenAPI 3.1 document to a file and validate it.

Usage: python scripts/export_openapi.py [output path]   (default: ../docs/openapi.json)
"""

from __future__ import annotations

import json
import os
import sys
from pathlib import Path

os.environ.setdefault("DATABASE_URL", "sqlite://")
os.environ.setdefault("REDIS_URL", "")
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from app.main import app


def main() -> None:
    out = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(__file__).resolve().parents[2] / "docs" / "openapi.json"
    spec = app.openapi()
    try:
        from openapi_spec_validator import validate

        validate(spec)
        print("OpenAPI document is valid")
    except ImportError:
        print("openapi-spec-validator not installed; skipping validation")
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(spec, indent=2) + "\n")
    print(f"wrote {out} ({len(spec['paths'])} paths, {len(spec['components']['schemas'])} schemas)")


if __name__ == "__main__":
    main()
