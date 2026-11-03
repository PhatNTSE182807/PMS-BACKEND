"""Trains the two detectors of the plate reader and copies the best weights to models/.

    python train.py --data datasets --epochs 60

1. Plate detector (datasets/plate.yaml): finds plates in a full camera frame -> models/plate.pt
2. Character detector (datasets/chars.yaml): finds each character in a plate crop -> models/chars.pt
"""

import argparse
import shutil
from pathlib import Path

from ultralytics import YOLO

from anpr_common import CHARS_IMGSZ, PLATE_IMGSZ


def train(base: str, data: Path, imgsz: int, epochs: int, batch: int, device: str, name: str, **augment) -> Path:
    model = YOLO(base)
    model.train(
        data=str(data),
        imgsz=imgsz,
        epochs=epochs,
        batch=batch,
        device=device or None,
        project="runs",
        name=name,
        exist_ok=True,
        # Never mirror plates: a mirrored character is not a character
        fliplr=0.0,
        flipud=0.0,
        **augment,
    )
    return Path(model.trainer.save_dir) / "weights" / "best.pt"


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--data", default="datasets", help="folder made by prepare_dataset.py")
    parser.add_argument("--model", default="yolo11n.pt", help="starting weights (downloaded by ultralytics)")
    parser.add_argument("--epochs", type=int, default=60)
    parser.add_argument("--batch", type=int, default=16)
    parser.add_argument("--device", default="", help="'' picks the GPU when there is one, else 'cpu'")
    parser.add_argument("--only", choices=["plate", "chars"], help="train one detector only")
    args = parser.parse_args()

    data = Path(args.data)
    models = Path("models")
    models.mkdir(exist_ok=True)

    if args.only in (None, "plate"):
        best = train(args.model, data / "plate.yaml", PLATE_IMGSZ, args.epochs, args.batch, args.device, "plate",
                     degrees=3.0, translate=0.1, scale=0.5, hsv_v=0.5, mosaic=1.0)
        shutil.copyfile(best, models / "plate.pt")
        print(f"Plate detector saved to {models / 'plate.pt'}")

    if args.only in (None, "chars"):
        best = train(args.model, data / "chars.yaml", CHARS_IMGSZ, args.epochs, args.batch, args.device, "chars",
                     degrees=4.0, translate=0.05, scale=0.25, shear=3.0, hsv_v=0.5, mosaic=0.0)
        shutil.copyfile(best, models / "chars.pt")
        print(f"Character detector saved to {models / 'chars.pt'}")


if __name__ == "__main__":
    main()
