# Cloud Run で動かすイメージ。経路パックを焼き込むので、地図データ本体は入らない
FROM python:3.13-slim

WORKDIR /srv
COPY requirements.txt ./
RUN pip install --no-cache-dir -r requirements.txt

COPY app ./app
COPY station_data/pack.json ./station_data/pack.json

RUN useradd --create-home appuser
USER appuser

ENV PORT=8080
CMD ["sh", "-c", "exec uvicorn app.main:create_app --factory --host 0.0.0.0 --port ${PORT}"]
