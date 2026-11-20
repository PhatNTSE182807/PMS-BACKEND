"""The two ways to run a trained detector: the .pt file with ultralytics, or the .onnx file with onnxruntime.

Both return the same thing, so the plate reader does not care which one it got:
a list of (x0, y0, x1, y1, confidence, class name), in pixels of the picture given.
The ONNX one needs only onnxruntime, OpenCV and NumPy, which is what the service on a server installs.
"""

import ast
import os
from pathlib import Path
from typing import Protocol

import cv2
import numpy as np

Detection = tuple[float, float, float, float, float, str]

# Same defaults as ultralytics predict, so .pt and .onnx give the same boxes
NMS_IOU = 0.7
MAX_DETECTIONS = 300
PAD_GREY = 114


class Detector(Protocol):
    def detect(self, image: np.ndarray, imgsz: int, conf: float) -> list[Detection]: ...


class UltralyticsDetector:
    """A .pt detector, run with ultralytics (needs PyTorch): used for training and on the training machine."""

    def __init__(self, path: str, device: str | None = None):
        from ultralytics import YOLO  # imported here so the ONNX service never needs ultralytics

        self.model = YOLO(path)
        self.device = device

    def detect(self, image: np.ndarray, imgsz: int, conf: float) -> list[Detection]:
        result = self.model.predict(image, imgsz=imgsz, conf=conf, device=self.device, verbose=False)[0]
        names = result.names
        found = []
        for box in result.boxes:
            x0, y0, x1, y1 = (float(v) for v in box.xyxy[0])
            found.append((x0, y0, x1, y1, float(box.conf[0]), names[int(box.cls[0])]))
        return found


class OnnxDetector:
    """A .onnx detector exported by export_onnx.py, run with onnxruntime on the CPU."""

    def __init__(self, path: str, threads: int | None = None):
        import onnxruntime as ort

        # ANPR_THREADS caps the CPU threads; set it to the CPUs of a small server (it may see more cores than it gets)
        threads = int(os.environ.get("ANPR_THREADS", "0")) if threads is None else threads
        options = ort.SessionOptions()
        if threads:
            options.intra_op_num_threads = threads
        self.session = ort.InferenceSession(path, options, providers=["CPUExecutionProvider"])
        self.input_name = self.session.get_inputs()[0].name
        # Exported with a fixed square input, e.g. [1, 3, 640, 640]
        self.size = int(self.session.get_inputs()[0].shape[2])
        # ultralytics writes the class names into the file's metadata, e.g. "{0: 'plate'}"
        meta = self.session.get_modelmeta().custom_metadata_map
        self.names: dict[int, str] = ast.literal_eval(meta["names"]) if "names" in meta else {}

    def _letterbox(self, image: np.ndarray) -> tuple[np.ndarray, float, float, float]:
        """Resizes the picture into the square input keeping its shape, grey bars on the sides (like ultralytics)."""
        height, width = image.shape[:2]
        ratio = min(self.size / height, self.size / width)
        new_w, new_h = int(round(width * ratio)), int(round(height * ratio))
        pad_w, pad_h = (self.size - new_w) / 2, (self.size - new_h) / 2
        if (new_w, new_h) != (width, height):
            image = cv2.resize(image, (new_w, new_h), interpolation=cv2.INTER_LINEAR)
        top, bottom = int(round(pad_h - 0.1)), int(round(pad_h + 0.1))
        left, right = int(round(pad_w - 0.1)), int(round(pad_w + 0.1))
        image = cv2.copyMakeBorder(image, top, bottom, left, right, cv2.BORDER_CONSTANT, value=(PAD_GREY,) * 3)
        return image, ratio, left, top

    def detect(self, image: np.ndarray, imgsz: int, conf: float) -> list[Detection]:
        # imgsz is fixed by the exported file; the argument is kept so both detectors are called the same way
        boxed, ratio, left, top = self._letterbox(image)
        tensor = cv2.cvtColor(boxed, cv2.COLOR_BGR2RGB).transpose(2, 0, 1)[None].astype(np.float32) / 255.0
        # Output [1, 4 + classes, candidates]: centre x, centre y, width, height, then one score per class
        output = self.session.run(None, {self.input_name: tensor})[0][0].T
        scores = output[:, 4:]
        classes = scores.argmax(axis=1)
        best = scores[np.arange(len(scores)), classes]
        keep = best > conf
        if not keep.any():
            return []
        boxes, best, classes = output[keep, :4], best[keep], classes[keep]

        # Per-class non-maximum suppression, as ultralytics does by default
        xywh = np.column_stack([boxes[:, 0] - boxes[:, 2] / 2, boxes[:, 1] - boxes[:, 3] / 2, boxes[:, 2], boxes[:, 3]])
        picked = cv2.dnn.NMSBoxesBatched(xywh.tolist(), best.tolist(), classes.tolist(), conf, NMS_IOU)
        picked = sorted(np.array(picked).flatten().tolist(), key=lambda i: -best[i])[:MAX_DETECTIONS]

        height, width = image.shape[:2]
        found = []
        for i in picked:
            x, y, w, h = xywh[i]
            # Back from the square input to the picture's own pixels
            x0 = float(np.clip((x - left) / ratio, 0, width))
            y0 = float(np.clip((y - top) / ratio, 0, height))
            x1 = float(np.clip((x + w - left) / ratio, 0, width))
            y1 = float(np.clip((y + h - top) / ratio, 0, height))
            cls = int(classes[i])
            found.append((x0, y0, x1, y1, float(best[i]), self.names.get(cls, str(cls))))
        return found


def load_detector(path: str, device: str | None = None) -> Detector:
    """The right detector for a model file: .onnx -> onnxruntime, anything else -> ultralytics."""
    if Path(path).suffix.lower() == ".onnx":
        return OnnxDetector(path)
    return UltralyticsDetector(path, device)
