"""Measures the plate reader on labelled images (a folder from the training images page).

    python evaluate.py --src D:/anpr/test-set

Prints how many plates were read exactly, how many were read with a confidence at or above the gate's
threshold (0.85, below it the Staff confirms the plate), and the misreads.
"""

import argparse
import json
from pathlib import Path

import cv2

from anpr_common import normalise
from read_plate import PlateReader


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--src", required=True)
    parser.add_argument("--threshold", type=float, default=0.85)
    parser.add_argument("--limit", type=int, default=0, help="only the first N images (0 = all)")
    args = parser.parse_args()

    reader = PlateReader()
    labels = sorted(Path(args.src, "labels").glob("*.json"))
    if args.limit:
        labels = labels[: args.limit]

    total = exact = confident = confident_wrong = 0
    misreads = []
    for label_path in labels:
        data = json.loads(label_path.read_text(encoding="utf-8"))
        image = cv2.imread(str(Path(args.src) / data["image"]))
        if image is None or not data.get("plateBox"):
            continue
        total += 1
        reads = reader.read(image)
        best = reads[0] if reads else None
        ok = best is not None and normalise(best.plate) == normalise(data["plate"])
        exact += ok
        if best and best.confidence >= args.threshold:
            confident += 1
            confident_wrong += not ok
        if not ok:
            misreads.append((data["plate"], best.plate if best else "(none)", best.confidence if best else 0.0))

    if not total:
        raise SystemExit("No labelled images found.")
    print(f"Images: {total}")
    print(f"Read exactly: {exact} ({exact / total:.1%})")
    print(f"At or above {args.threshold}: {confident} ({confident / total:.1%}), of which wrong: {confident_wrong}")
    for expected, got, conf in misreads[:25]:
        print(f"  expected {expected:<14} read {got:<14} confidence {conf:.2f}")


if __name__ == "__main__":
    main()
