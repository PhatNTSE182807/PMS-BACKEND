"""Checks this machine before training: Python, PyTorch, the GPU and the tool's libraries.

    python check_env.py

It says whether training will run on the NVIDIA GPU or on the CPU, and what to do about it.
"""

import platform
import shutil
import subprocess
import sys


def nvidia_gpu() -> str | None:
    """Name and memory of the NVIDIA card seen by the driver, or None."""
    if not shutil.which("nvidia-smi"):
        return None
    try:
        out = subprocess.run(
            ["nvidia-smi", "--query-gpu=name,memory.total", "--format=csv,noheader"],
            capture_output=True, text=True, timeout=10, check=True,
        ).stdout.strip()
    except (OSError, subprocess.SubprocessError):
        return None
    return out.splitlines()[0] if out else None


def main() -> None:
    ok = True
    print(f"Python      {platform.python_version()} ({sys.executable})")
    if not (3, 10) <= sys.version_info[:2] <= (3, 12):
        print("            ! Use Python 3.10, 3.11 or 3.12.")
        ok = False

    for name in ("ultralytics", "cv2", "numpy", "fastapi", "uvicorn"):
        try:
            module = __import__(name)
            print(f"{name:<11} {getattr(module, '__version__', 'installed')}")
        except ImportError:
            print(f"{name:<11} ! missing: run  pip install -r requirements.txt")
            ok = False

    card = nvidia_gpu()
    print(f"NVIDIA card {card or 'none found'}")

    try:
        import torch
    except ImportError:
        print("PyTorch     ! missing: run  pip install -r requirements.txt")
        sys.exit(1)

    cuda = torch.cuda.is_available()
    build = torch.version.cuda or "CPU only"
    print(f"PyTorch     {torch.__version__} (CUDA build: {build})")

    print()
    if cuda:
        props = torch.cuda.get_device_properties(0)
        memory = props.total_memory / 1024**3
        batch = 32 if memory >= 10 else 16 if memory >= 5 else 8
        print(f"Training runs on the GPU: {props.name}, {memory:.0f} GB.")
        print(f"Suggested:  python train.py --data datasets --epochs 40 --device 0 --batch {batch}")
    else:
        print("Training runs on the CPU (slow: about an hour for 1,000 images and 15 epochs).")
        if card:
            print("This machine has an NVIDIA card but PyTorch cannot use it: the CPU-only build is installed.")
            print("Fix it (see README, 'Use the NVIDIA GPU'):")
            print("  pip uninstall -y torch torchvision")
            print("  then the install command from pytorch.org (Stable, Windows, Pip, Python, CUDA 12.x)")
        else:
            print("No NVIDIA card: keep the CPU and train lighter:")
        print("Suggested:  python train.py --data datasets --epochs 15 --device cpu --batch 8")

    sys.exit(0 if ok else 1)


if __name__ == "__main__":
    main()
