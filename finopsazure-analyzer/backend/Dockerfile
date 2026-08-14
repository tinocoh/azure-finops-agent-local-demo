FROM python:3.14-slim

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PIP_NO_CACHE_DIR=1

WORKDIR /app

# Install dependencies first for better layer caching.
COPY requirements.txt ./
RUN pip install --no-cache-dir -r requirements.txt

COPY app ./app

# Non-root user for runtime.
RUN useradd -m appuser && mkdir -p /app/data && chown -R appuser:appuser /app
USER appuser

EXPOSE 8000

# Uvicorn serves the FastAPI app. Data dir is a mounted volume for SQLite.
CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8000"]
