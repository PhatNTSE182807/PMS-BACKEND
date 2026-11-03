"""Plate reader service for the gate screens.

    uvicorn service:app --port 8001

GET  /health  -> whether the trained models are there
POST /read    -> body: one camera frame (image/jpeg or image/png); returns the plates read in it

The models (models/plate.pt and models/chars.pt) are loaded on the first read and loaded again when
the files change, so a model trained while the service runs is picked up without a restart.
Plates only go to the Staff screens that call this service on the lot's own network; set the
allowed screen origins with ANPR_ALLOWED_ORIGINS (comma-separated).
"""

import os
import threading
from dataclasses import asdict
from pathlib import Path

import cv2
import numpy as np
from fastapi import FastAPI, HTTPException, Request
from fastapi.middleware.cors import CORSMiddleware

from read_plate import PlateReader

MODELS = Path(os.environ.get("ANPR_MODELS", Path(__file__).parent / "models"))
PLATE_MODEL = MODELS / "plate.pt"
CHARS_MODEL = MODELS / "chars.pt"
MAX_IMAGE_BYTES = 8 * 1024 * 1024
ORIGINS = [o.strip() for o in os.environ.get("ANPR_ALLOWED_ORIGINS", "http://localhost:5174,http://127.0.0.1:5174").split(",") if o.strip()]

app = FastAPI(title="XXIV Parking plate reader")
app.add_middleware(CORSMiddleware, allow_origins=ORIGINS, allow_methods=["GET", "POST"], allow_headers=["Content-Type"])

_lock = threading.Lock()
_reader: PlateReader | None = None
_loaded_stamp: tuple[float, float] | None = None


def _stamp() -> tuple[float, float] | None:
    if not (PLATE_MODEL.exists() and CHARS_MODEL.exists()):
        return None
    return PLATE_MODEL.stat().st_mtime, CHARS_MODEL.stat().st_mtime


def _get_reader() -> PlateReader:
    """The reader for the current model files; loads them again after a new training run."""
    global _reader, _loaded_stamp
    stamp = _stamp()
    if stamp is None:
        raise HTTPException(status_code=503, detail="The plate reader is not trained yet: models/plate.pt and models/chars.pt are missing.")
    with _lock:
        if _reader is None or stamp != _loaded_stamp:
            _reader = PlateReader(str(PLATE_MODEL), str(CHARS_MODEL))
            _loaded_stamp = stamp
        return _reader


@app.get("/health")
def health() -> dict:
    return {"ready": _stamp() is not None, "plateModel": PLATE_MODEL.exists(), "charsModel": CHARS_MODEL.exists()}


@app.post("/read")
async def read(request: Request) -> dict:
    body = await request.body()
    if not body:
        raise HTTPException(status_code=400, detail="Send one camera frame (JPEG or PNG) as the request body.")
    if len(body) > MAX_IMAGE_BYTES:
        raise HTTPException(status_code=413, detail="The frame is larger than 8 MB.")
    image = cv2.imdecode(np.frombuffer(body, dtype=np.uint8), cv2.IMREAD_COLOR)
    if image is None:
        raise HTTPException(status_code=400, detail="The frame is not a JPEG or PNG image.")
    reader = _get_reader()
    with _lock:
        plates = reader.read(image)
    return {"plates": [asdict(p) for p in plates]}
