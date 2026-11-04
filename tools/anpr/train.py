"""Trains the two detectors of the plate reader and copies the best weights to models/.

    python train.py --data datasets --epochs 20
    python train.py --data datasets --epochs 20 --resume     # carry on after Ctrl+C

1. Plate detector (datasets/plate.yaml): finds plates in a full camera frame -> models/plate.pt
2. Character detector (datasets/chars.yaml): finds each character in a plate crop -> models/chars.pt

Every epoch is saved to runs/<plate|chars>/weights/last.pt, so training can be stopped with Ctrl+C and
continued later with --resume, exactly where it stopped (same result as one run).
"""

import argparse
import shutil
from pathlib import Path

from ultralytics import YOLO

from anpr_common import CHARS_IMGSZ, PLATE_IMGSZ

RUNS = Path("runs")


def train(base: str, data: Path, imgsz: int, epochs: int, batch: int, device: str, workers: int, name: str, resume: bool, **augment) -> Path:
    """Trains one detector, or continues its stopped run with `resume`; returns its best weights."""
    run = RUNS / name
    last = run / "weights" / "last.pt"
    best = run / "weights" / "best.pt"

    if resume and last.exists():
        print(f"Continuing the {name} run from {last} (its epochs, batch and workers stay as first chosen).")
        try:
            YOLO(str(last)).train(resume=True)
        except AssertionError as e:
            # ultralytics refuses to resume a run that already reached its last epoch
            if "nothing to resume" not in str(e):
                raise
            print(f"The {name} run is already finished: keeping its best weights.")
        return best

    if resume:
        print(f"No stopped {name} run to continue: starting it from {base}.")
    model = YOLO(base)
    model.train(
        data=str(data),
        imgsz=imgsz,
        epochs=epochs,
        batch=batch,
        device=device or None,
        # Few image loaders: on Windows each one is a separate Python process that loads PyTorch again (RAM)
        workers=workers,
        cache=False,
        project=str(RUNS),
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
    parser.add_argument("--epochs", type=int, default=20)
    parser.add_argument("--batch", type=int, default=16, help="images per step; lower it (8, 4) when RAM or video memory runs out")
    parser.add_argument("--workers", type=int, default=2, help="image loader processes; each costs RAM (0 = none, slowest but lightest)")
    parser.add_argument("--device", default="", help="0 = first NVIDIA GPU, cpu = processor; empty picks the GPU when PyTorch can use it")
    parser.add_argument("--only", choices=["plate", "chars"], help="train one detector only")
    parser.add_argument("--resume", action="store_true", help="continue runs stopped with Ctrl+C instead of starting again")
    args = parser.parse_args()
    if args.workers < 0 or args.batch < 1 or args.epochs < 1:
        raise SystemExit("--workers must be 0 or more, --batch and --epochs 1 or more.")

    import torch

    gpu = torch.cuda.is_available()
    if args.device and args.device != "cpu" and not gpu:
        raise SystemExit(
            f"--device {args.device} asks for the GPU, but PyTorch cannot use one (build: {torch.__version__}).\n"
            "Run  python check_env.py  to see why, or train with  --device cpu."
        )
    if not args.device:
        args.device = "0" if gpu else "cpu"
    if args.device == "cpu":
        print("Training on the CPU: this is slow. Run  python check_env.py  to see whether the GPU can be used.")
    else:
        print(f"Training on the GPU: {torch.cuda.get_device_name(int(args.device.split(',')[0]))}")
    print(f"Batch {args.batch}, {args.workers} image loader(s). Stop with Ctrl+C; continue later with --resume.")

    data = Path(args.data)
    if not (data / "plate.yaml").exists():
        raise SystemExit(f"{data / 'plate.yaml'} not found: run  python prepare_dataset.py --src generated --out {data}  first.")
    models = Path("models")
    models.mkdir(exist_ok=True)

    common = dict(epochs=args.epochs, batch=args.batch, device=args.device, workers=args.workers, resume=args.resume)
    if args.only in (None, "plate"):
        best = train(args.model, data / "plate.yaml", PLATE_IMGSZ, name="plate", **common,
                     degrees=3.0, translate=0.1, scale=0.5, hsv_v=0.5, mosaic=1.0)
        shutil.copyfile(best, models / "plate.pt")
        print(f"Plate detector saved to {models / 'plate.pt'}")

    if args.only in (None, "chars"):
        best = train(args.model, data / "chars.yaml", CHARS_IMGSZ, name="chars", **common,
                     degrees=4.0, translate=0.05, scale=0.25, shear=3.0, hsv_v=0.5, mosaic=0.0)
        shutil.copyfile(best, models / "chars.pt")
        print(f"Character detector saved to {models / 'chars.pt'}")


if __name__ == "__main__":
    main()
