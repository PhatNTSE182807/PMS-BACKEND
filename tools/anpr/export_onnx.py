"""Exports the two trained detectors to ONNX, for the plate reader service on a server.

    python export_onnx.py                  # models/plate.pt -> models/plate.onnx, models/chars.pt -> models/chars.onnx

The ONNX files hold the same trained weights as the .pt files. They run with onnxruntime alone, so the
service on a server does not need PyTorch or ultralytics (much smaller image, far less memory).
Train with the .pt files as before, then run this again after every new training run.
"""

import argparse
import functools
import inspect
import shutil
from pathlib import Path

import torch
from ultralytics import YOLO

# Recent PyTorch exports with its new exporter by default, which writes the weights to a side file and
# cannot target opset 17. The classic exporter writes one self-contained .onnx file.
if "dynamo" in inspect.signature(torch.onnx.export).parameters:
    torch.onnx.export = functools.partial(torch.onnx.export, dynamo=False)

from anpr_common import CHARS_IMGSZ, PLATE_IMGSZ


def export(source: Path, imgsz: int) -> Path:
    """One .pt detector -> a .onnx file next to it, with a fixed square input of `imgsz` pixels."""
    if not source.exists():
        raise SystemExit(f"{source} is missing: train the detectors first (python train.py --data datasets).")
    # Fixed input size and batch 1: what the service sends; opset 17 runs on any recent onnxruntime
    written = Path(YOLO(str(source)).export(format="onnx", imgsz=imgsz, opset=17, simplify=True, dynamic=False))
    target = source.with_suffix(".onnx")
    if written.resolve() != target.resolve():
        shutil.move(str(written), target)
    print(f"{source} -> {target} ({target.stat().st_size / 1e6:.1f} MB)")
    return target


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--models", default="models", help="folder with plate.pt and chars.pt")
    args = parser.parse_args()

    models = Path(args.models)
    export(models / "plate.pt", PLATE_IMGSZ)
    export(models / "chars.pt", CHARS_IMGSZ)


if __name__ == "__main__":
    main()
