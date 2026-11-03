"""Turns folders made by the frontend's "Plate reader training images" page into two YOLO datasets.

    python prepare_dataset.py --src D:/anpr/generated [more folders...] --out datasets

Each source folder holds images/<name>.jpg and labels/<name>.json (plate box and character boxes,
0..1 from the top-left corner). The output has:

    datasets/plate/  full frames, one class "plate"           (plate.yaml)
    datasets/chars/  plate crops, one class per character      (chars.yaml)
"""

import argparse
import json
import random
import shutil
from pathlib import Path

import cv2

from anpr_common import CHAR_CLASSES, CHAR_INDEX, CROP_MARGIN


def yolo_line(class_id: int, cx: float, cy: float, w: float, h: float) -> str:
    clamp = lambda v: min(1.0, max(0.0, v))  # noqa: E731
    return f"{class_id} {clamp(cx):.6f} {clamp(cy):.6f} {clamp(w):.6f} {clamp(h):.6f}"


def make_dirs(root: Path) -> None:
    for kind in ("images", "labels"):
        for split in ("train", "val"):
            (root / kind / split).mkdir(parents=True, exist_ok=True)


def write_yaml(path: Path, root: Path, names: list[str]) -> None:
    lines = [f"path: {root.resolve().as_posix()}", "train: images/train", "val: images/val", "names:"]
    lines += [f"  {i}: '{name}'" for i, name in enumerate(names)]
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")


def crop_plate(image, box: dict, margin: float):
    """Cuts the plate out with a margin; returns the crop and its (x, y, w, h) in pixels."""
    height, width = image.shape[:2]
    bw, bh = box["width"] * width, box["height"] * height
    x0 = max(0, int(box["x"] * width - bw * margin))
    y0 = max(0, int(box["y"] * height - bh * margin))
    x1 = min(width, int((box["x"] + box["width"]) * width + bw * margin))
    y1 = min(height, int((box["y"] + box["height"]) * height + bh * margin))
    return image[y0:y1, x0:x1], (x0, y0, x1 - x0, y1 - y0)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--src", nargs="+", required=True, help="folders saved by the training images page")
    parser.add_argument("--out", default="datasets", help="output folder (default: datasets)")
    parser.add_argument("--val", type=float, default=0.15, help="share of images kept for validation")
    parser.add_argument("--seed", type=int, default=42)
    parser.add_argument("--min-plate-px", type=int, default=12, help="skip plates smaller than this (pixels high)")
    args = parser.parse_args()

    random.seed(args.seed)
    out = Path(args.out)
    plate_root, chars_root = out / "plate", out / "chars"
    for root in (plate_root, chars_root):
        if root.exists():
            shutil.rmtree(root)
        make_dirs(root)

    labels = [p for src in args.src for p in sorted(Path(src, "labels").glob("*.json"))]
    if not labels:
        raise SystemExit("No label files found: point --src at folders that contain labels/ and images/.")

    counts = {"train": 0, "val": 0, "skipped": 0, "chars": 0}
    for label_path in labels:
        data = json.loads(label_path.read_text(encoding="utf-8"))
        image_path = label_path.parent.parent / data["image"]
        image = cv2.imread(str(image_path))
        box = data.get("plateBox")
        if image is None or not box or box["height"] * image.shape[0] < args.min_plate_px:
            counts["skipped"] += 1
            continue

        split = "val" if random.random() < args.val else "train"
        name = f"{label_path.parent.parent.name}_{label_path.stem}"

        # Plate detector: the whole frame, one box
        shutil.copyfile(image_path, plate_root / "images" / split / f"{name}.jpg")
        line = yolo_line(0, box["x"] + box["width"] / 2, box["y"] + box["height"] / 2, box["width"], box["height"])
        (plate_root / "labels" / split / f"{name}.txt").write_text(line + "\n", encoding="utf-8")

        # Character detector: the plate crop (with a random margin), one box per character
        crop, (cx0, cy0, cw, ch) = crop_plate(image, box, CROP_MARGIN * random.uniform(0.5, 2.0))
        if cw < 8 or ch < 8:
            counts["skipped"] += 1
            continue
        height, width = image.shape[:2]
        lines = []
        for c in data.get("chars", []):
            class_id = CHAR_INDEX.get(c["char"])
            if class_id is None:
                continue
            x = (c["x"] * width - cx0) / cw
            y = (c["y"] * height - cy0) / ch
            w, h = c["width"] * width / cw, c["height"] * height / ch
            if x + w <= 0 or y + h <= 0 or x >= 1 or y >= 1:
                continue
            lines.append(yolo_line(class_id, x + w / 2, y + h / 2, w, h))
        cv2.imwrite(str(chars_root / "images" / split / f"{name}.jpg"), crop)
        (chars_root / "labels" / split / f"{name}.txt").write_text("\n".join(lines) + "\n", encoding="utf-8")
        counts[split] += 1
        counts["chars"] += len(lines)

    write_yaml(out / "plate.yaml", plate_root, ["plate"])
    write_yaml(out / "chars.yaml", chars_root, CHAR_CLASSES)
    print(f"Train {counts['train']}, val {counts['val']}, skipped {counts['skipped']}, characters {counts['chars']}.")
    print(f"Datasets written to {out.resolve()}")


if __name__ == "__main__":
    main()
