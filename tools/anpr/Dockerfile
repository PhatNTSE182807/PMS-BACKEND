# Plate reader service with the ONNX models, for a small CPU server (e.g. Render).
# Build from tools/anpr after `python export_onnx.py`:  docker build -t pms-anpr .
FROM python:3.12-slim

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    ANPR_MODELS=/app/models \
    ANPR_THREADS=1

WORKDIR /app

COPY requirements-service.txt .
RUN pip install --no-cache-dir -r requirements-service.txt

# Only the code the service imports, and the two ONNX models (no .pt files, no datasets)
COPY anpr_common.py detectors.py read_plate.py service.py ./
COPY models/plate.onnx models/chars.onnx ./models/

RUN useradd --create-home anpr
USER anpr

EXPOSE 8001
# Render gives the port in PORT; 8001 when run elsewhere
CMD ["sh", "-c", "uvicorn service:app --host 0.0.0.0 --port ${PORT:-8001}"]
