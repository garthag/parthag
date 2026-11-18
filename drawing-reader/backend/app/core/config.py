"""Application configuration loaded from environment variables."""

from __future__ import annotations

from functools import lru_cache
from pathlib import Path
from typing import Literal

from pydantic import Field, field_validator
from pydantic_settings import BaseSettings, SettingsConfigDict


class Settings(BaseSettings):
    """Runtime settings. Every value can be overridden with an env var of the same name."""

    model_config = SettingsConfigDict(env_file=".env", env_file_encoding="utf-8", extra="ignore")

    APP_NAME: str = "Drawing Reader"
    APP_VERSION: str = "1.0.0"
    ENVIRONMENT: Literal["development", "test", "staging", "production"] = "development"
    DEBUG: bool = False
    LOG_LEVEL: str = "INFO"
    LOG_JSON: bool = False
    API_PREFIX: str = "/api"

    # Database
    DATABASE_URL: str = "postgresql+psycopg://drawing:drawing@localhost:5432/drawing_reader"
    DB_POOL_SIZE: int = 10
    DB_MAX_OVERFLOW: int = 20
    DB_ECHO: bool = False

    # Redis / Celery
    REDIS_URL: str | None = "redis://localhost:6379/0"
    CELERY_BROKER_URL: str | None = None
    CELERY_RESULT_BACKEND: str | None = None
    # When True OCR runs in-process (FastAPI background task) instead of Celery.
    OCR_EAGER: bool = True

    # Security
    SECRET_KEY: str = Field(default="change-me-in-production-please-32chars-min", min_length=32)
    JWT_ALGORITHM: str = "HS256"
    ACCESS_TOKEN_EXPIRE_MINUTES: int = 30
    REFRESH_TOKEN_EXPIRE_MINUTES: int = 60 * 24 * 7
    CORS_ORIGINS: list[str] = ["http://localhost:5173", "http://localhost:8080", "http://localhost"]

    # Storage
    STORAGE_DIR: Path = Path("./storage")
    MAX_UPLOAD_MB: int = 200
    UPLOAD_BATCH_COMMIT: int = 50

    # OCR
    OCR_ENGINE: Literal["auto", "paddle", "tesseract", "none"] = "auto"
    OCR_DPI: int = 300
    OCR_MIN_NATIVE_CHARS: int = 40
    OCR_CONFIDENCE_THRESHOLD: float = 0.9

    # Costing
    DEFAULT_CURRENCY: str = "USD"
    DEFAULT_STEEL_RATE_PER_KG: float = 2.5

    # AI assistant (optional LLM fallback for intent parsing)
    ANTHROPIC_API_KEY: str | None = None
    AI_MODEL: str = "claude-opus-5-5"
    AI_TIMEOUT_SECONDS: float = 4.0

    # Dashboard cache TTL (seconds)
    CACHE_TTL_SECONDS: int = 30

    @field_validator("CORS_ORIGINS", mode="before")
    @classmethod
    def _split_origins(cls, value: object) -> object:
        if isinstance(value, str) and not value.startswith("["):
            return [v.strip() for v in value.split(",") if v.strip()]
        return value

    @property
    def broker_url(self) -> str | None:
        return self.CELERY_BROKER_URL or self.REDIS_URL

    @property
    def result_backend(self) -> str | None:
        return self.CELERY_RESULT_BACKEND or self.REDIS_URL

    @property
    def is_sqlite(self) -> bool:
        return self.DATABASE_URL.startswith("sqlite")


@lru_cache
def get_settings() -> Settings:
    return Settings()


settings = get_settings()
