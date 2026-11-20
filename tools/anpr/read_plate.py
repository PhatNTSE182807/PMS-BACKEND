"""Reads Vietnamese number plates in a picture with the two trained detectors.

    python read_plate.py frame.jpg                 # prints the plates found, as JSON
    python read_plate.py frame.jpg --save out.jpg  # also draws them on a copy of the picture
    python read_plate.py frame.jpg --plate-model models/plate.onnx --chars-model models/chars.onnx

`PlateReader` is the part the gate service reuses. It takes .pt files (ultralytics) or .onnx files
(onnxruntime, see export_onnx.py) and reads the same way with both.
"""

import argparse
import json
from dataclasses import asdict, dataclass

import cv2
import numpy as np

from anpr_common import CHARS_IMGSZ, CROP_MARGIN, PLATE_IMGSZ, format_plate
from detectors import load_detector


@dataclass
class PlateRead:
    plate: str
    """Printed form, e.g. "51K-889.24"; empty when no character could be read."""
    confidence: float
    """0..1: plate detection confidence times the mean character confidence."""
    box: tuple[int, int, int, int]
    """x0, y0, x1, y1 in pixels."""
    rows: list[str]


def group_rows(chars: list[tuple[str, float, float, float, float]]) -> list[str]:
    """Characters (char, conf, centre x, centre y, height) -> text rows, top to bottom, left to right.

    A motorbike plate has two rows: a new row starts where the vertical gap between characters
    is larger than half a character's height.
    """
    if not chars:
        return []
    by_y = sorted(chars, key=lambda c: c[3])
    height = float(np.median([c[4] for c in chars]))
    rows, current = [], [by_y[0]]
    for c in by_y[1:]:
        if c[3] - current[-1][3] > height * 0.5:
            rows.append(current)
            current = [c]
        else:
            current.append(c)
    rows.append(current)
    return ["".join(c[0] for c in sorted(row, key=lambda c: c[2])) for row in rows]


class PlateReader:
    def __init__(self, plate_model: str = "models/plate.pt", chars_model: str = "models/chars.pt",
                 plate_conf: float = 0.35, char_conf: float = 0.25, device: str | None = None):
        self.plates = load_detector(plate_model, device)
        self.chars = load_detector(chars_model, device)
        self.plate_conf = plate_conf
        self.char_conf = char_conf

    def read(self, image: np.ndarray) -> list[PlateRead]:
        """Every plate found in a BGR picture, the most confident first."""
        height, width = image.shape[:2]
        reads = []
        for x0, y0, x1, y1, plate_conf, _ in self.plates.detect(image, PLATE_IMGSZ, self.plate_conf):
            mx, my = (x1 - x0) * CROP_MARGIN, (y1 - y0) * CROP_MARGIN
            cx0, cy0 = max(0, int(x0 - mx)), max(0, int(y0 - my))
            cx1, cy1 = min(width, int(x1 + mx)), min(height, int(y1 + my))
            crop = image[cy0:cy1, cx0:cx1]
            if crop.size == 0:
                continue

            chars = [
                (name, conf, (a0 + a1) / 2, (b0 + b1) / 2, b1 - b0)
                for a0, b0, a1, b1, conf, name in self.chars.detect(crop, CHARS_IMGSZ, self.char_conf)
            ]
            rows = group_rows(chars)
            char_conf = float(np.mean([c[1] for c in chars])) if chars else 0.0
            reads.append(PlateRead(
                plate=format_plate(rows),
                confidence=round(plate_conf * char_conf, 4),
                box=(int(x0), int(y0), int(x1), int(y1)),
                rows=rows,
            ))
        return sorted(reads, key=lambda r: r.confidence, reverse=True)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("image")
    parser.add_argument("--plate-model", default="models/plate.pt")
    parser.add_argument("--chars-model", default="models/chars.pt")
    parser.add_argument("--save", help="write a copy of the picture with the plates drawn on it")
    args = parser.parse_args()

    image = cv2.imread(args.image)
    if image is None:
        raise SystemExit(f"Cannot open {args.image}")
    reads = PlateReader(args.plate_model, args.chars_model).read(image)
    print(json.dumps([asdict(r) for r in reads], ensure_ascii=False, indent=2))

    if args.save:
        for r in reads:
            x0, y0, x1, y1 = r.box
            cv2.rectangle(image, (x0, y0), (x1, y1), (94, 197, 34), 2)
            cv2.putText(image, f"{r.plate} {r.confidence:.2f}", (x0, max(18, y0 - 6)), cv2.FONT_HERSHEY_SIMPLEX, 0.6, (94, 197, 34), 2)
        cv2.imwrite(args.save, image)


if __name__ == "__main__":
    main()
