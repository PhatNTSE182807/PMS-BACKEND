# Plate reader (ANPR) tools

Train a Vietnamese number plate reader on images rendered from the 3D gate lanes, then read
plates in a picture or from the gate screens. Two YOLO detectors work one after the other:

1. **Plate detector:** finds the plate in a full camera frame.
2. **Character detector:** finds each character in the plate crop (30 classes: `0-9` and the
   series letters). Characters are put in rows (motorbike plates have two) and read left to right,
   then formatted as printed: `51K-889.24`, `59X1-234.56`.

The confidence of a read is the plate confidence times the mean character confidence. The gate
asks the Staff to confirm plates read below 0.85 (`Parking:Gate:AnprConfidenceThreshold`).

| File | What it does |
| --- | --- |
| `check_env.py` | Checks Python, the libraries and whether training will use the GPU |
| `prepare_dataset.py` | Turns generated images into the two YOLO datasets |
| `train.py` | Trains both detectors, saves `models/plate.pt` and `models/chars.pt` |
| `evaluate.py` | Measures the reader on a labelled test folder |
| `read_plate.py` | Reads the plates in one picture |
| `service.py` | HTTP service the Staff gate screen calls |

## Quick start (every command runs in `tools/anpr`)

```powershell
cd "D:\OJT Project\Parking system\tools\anpr"
python -m venv .venv                     # once
.venv\Scripts\Activate.ps1               # every new terminal
# NVIDIA card? install PyTorch with CUDA first: see "Use the NVIDIA GPU" below
pip install -r requirements.txt          # once
python check_env.py                      # says GPU or CPU and the train command to use
python prepare_dataset.py --src generated --out datasets
python train.py --data datasets          # 20 epochs, GPU when PyTorch can use it
python evaluate.py --src test-set
uvicorn service:app --port 8001          # for the Staff gate screen
```

## 1. Install Python (once per machine)

- Install **Python 3.12** (3.10 and 3.11 also work) from python.org, Windows installer (64-bit).
  On the first screen tick **Add python.exe to PATH**, then **Install Now**.
- If a terminal says *"Python was not found; run without arguments to install from the Microsoft
  Store"*: Settings → Apps → Advanced app settings → **App execution aliases**, turn off
  `python.exe` and `python3.exe`, then open a **new** terminal. `py -3.12` also works instead of `python`.
- Check: `python --version` shows `Python 3.12.x`.

## 2. Create the environment (once per machine)

```powershell
cd "D:\OJT Project\Parking system\tools\anpr"
python -m venv .venv
.venv\Scripts\Activate.ps1
```

The prompt now starts with `(.venv)`. Run `.venv\Scripts\Activate.ps1` again in every new terminal.
If PowerShell answers *"running scripts is disabled on this system"*, run this once and try again:

```powershell
Set-ExecutionPolicy -Scope CurrentUser RemoteSigned
```

Then install the libraries. **With an NVIDIA card, do step 3 first**: `pip install -r requirements.txt`
alone installs the CPU-only PyTorch on Windows.

```powershell
pip install -r requirements.txt
```

## 3. Use the NVIDIA GPU (machines with an NVIDIA card)

Training on the GPU takes minutes instead of an hour or more.

1. Check the card and its driver: `nvidia-smi` must list the card. If the command is missing,
   install the latest driver from nvidia.com (GeForce Experience / NVIDIA App also does it).
2. Run `python check_env.py`. If it says *"the CPU-only build is installed"*, replace PyTorch:
   ```powershell
   pip uninstall -y torch torchvision
   ```
   Then open **pytorch.org → Get Started**, choose *Stable, Windows, Pip, Python* and the newest
   **CUDA 12.x**, and run the command it shows, for example:
   ```powershell
   pip install torch torchvision --index-url https://download.pytorch.org/whl/cu126
   ```
   (about 2.5 GB). Stop any training that is running before you do this.
3. Run `python check_env.py` again: it must say *"Training runs on the GPU"* and suggest a batch size.

Tips for laptops: keep the charger plugged in (on battery the card slows down). With 4–6 GB of
video memory keep `--batch 16`; on *"CUDA out of memory"* use `--batch 8`.

## 4. Train on the CPU only (no NVIDIA card, or the GPU does not work)

Everything works on the CPU, only slower. Use fewer images and epochs and a smaller batch:

```powershell
python train.py --data datasets --epochs 15 --device cpu --batch 8
```

| Images | Epochs | CPU (approx.) | GPU, e.g. RTX 3060 (approx.) |
| --- | --- | --- | --- |
| 10 | 1 | a few minutes | under a minute: only checks that everything runs |
| 1,000 | 15 | about 1 hour | 5–15 minutes |
| 3,000 | 40 | several hours | 30–60 minutes |

`train.py` prints whether it trains on the GPU or the CPU. Asking for `--device 0` on a machine where
PyTorch cannot use the GPU stops at once with a message instead of failing later.

## 5. Make training images

Run the frontend in development (`npm run dev`) with the backend running, sign in as Admin and open
`/admin/anpr-dataset` (**Plate reader training images**, development builds only). Use Chrome or
Edge on a computer.

- **Preview a sample** draws one frame with its labels: plate box in green, character boxes in amber.
- **Choose folder and generate** saves `images/*.jpg` (1280 × 720) and `labels/*.json` into the
  folder you pick. Each label file has the plate text, the vehicle kind, the plate box and every
  character box (0..1 from the top-left corner).

Pick `tools/anpr/generated` for training images and `tools/anpr/test-set` for a separate test set
(about 200 images; never mix the two, or the test score is meaningless). New images are added next
to old ones; delete `images/` and `labels/` first to start again. 1,000 images are enough to start;
if letters are often misread, generate 1,000–2,000 more into another folder and pass both to
`prepare_dataset.py`.

Every frame has a random vehicle (car or motorbike, set the share), plate, position, angle,
camera pose and zoom, brightness, barrier position and plate dirt.

## 6. Build the datasets

```powershell
python prepare_dataset.py --src generated --out datasets
python prepare_dataset.py --src generated generated2 --out datasets   # several folders
```

Writes `datasets/plate.yaml` (full frames, one class) and `datasets/chars.yaml` (plate crops, one
class per character), each split into train and val. Run it again whenever you add images.

## 7. Train

```powershell
python train.py --data datasets                 # 20 epochs, GPU if possible
python train.py --data datasets --epochs 40     # longer, better
python train.py --data datasets --only chars    # one detector only
```

Starts from `yolo11n.pt` (downloaded by ultralytics) and saves `models/plate.pt` and
`models/chars.pt`. Each epoch prints a line: the **mAP50** column should climb towards 1.0; once it
stops rising, more epochs do not help. Charts are in `runs/`.

**Stop and carry on later.** Every epoch is saved to `runs/plate/weights/last.pt` (and `runs/chars/...`).
Press **Ctrl + C** to stop, then run the same command with `--resume`: training continues from the
epoch it reached, with the same result as one uninterrupted run. A run that already finished is kept as
it is. Do not split training into several fresh `--epochs 5` runs: each fresh run starts again from
`yolo11n.pt`.

```powershell
python train.py --data datasets --epochs 20        # Ctrl + C at any time
python train.py --data datasets --resume           # continues where it stopped
```

**Low on RAM (16 GB or less).** Each image loader is a separate Python process that loads PyTorch again
(several hundred MB each). `train.py` uses 2 by default; lower it, and the batch, when the machine
slows down, and close Visual Studio and other heavy apps while training:

```powershell
python train.py --data datasets --batch 8 --workers 1   # lighter
python train.py --data datasets --batch 4 --workers 0   # lightest, slowest
```

## 8. Read and measure

```powershell
python evaluate.py --src test-set
python read_plate.py test-set/images/<name>.jpg --save read.jpg
```

`evaluate.py` reports how many plates were read exactly and how many reads at or above 0.85 were
wrong (keep that one near zero). `read_plate.py` prints every plate found (text, confidence, box).

## 9. Run the service for the gate screens

```powershell
uvicorn service:app --port 8001
```

- `GET /health` says whether `models/plate.pt` and `models/chars.pt` are there.
- `POST /read` takes one camera frame (JPEG or PNG body) and returns the plates read in it.
- New model files are picked up on the next read, so training can finish while the service runs.
- Only the Staff screens in `ANPR_ALLOWED_ORIGINS` may call it (default `http://localhost:5174`).
- The service uses the GPU when PyTorch can, otherwise the CPU (a read then takes longer).

On the Staff gate dashboard each camera shows the plate reader's state (ready, not trained, offline).
When a vehicle stops at the camera, the frame (1280 × 720) is sent to the service and the camera
shows what the AI read next to the recorded plate: **Match**, **Staff check** (below 0.85) or
**Mismatch**, with a running "AI matched / read" count. The frontend calls
`http://127.0.0.1:8001` in development; set `VITE_ANPR_SERVICE_URL` to change it.

## Troubleshooting

| Message | Fix |
| --- | --- |
| `Python was not found ... Microsoft Store` | Install Python (step 1) and turn off the App execution aliases |
| `running scripts is disabled on this system` | `Set-ExecutionPolicy -Scope CurrentUser RemoteSigned` |
| `No label files found` | `--src` must point at the folder that holds `images/` and `labels/` |
| `datasets\plate.yaml not found` | Run `prepare_dataset.py` first |
| `--device 0 asks for the GPU, but PyTorch cannot use one` | Step 3, or train with `--device cpu` |
| `CUDA out of memory` | `--batch 8` (or 4) |
| The computer freezes or runs out of RAM while training | `--workers 1` (or 0) and `--batch 8`; close other apps |
| Training stopped half-way (Ctrl + C, crash, shutdown) | Run the same command with `--resume` |
| Camera shows *ANPR offline* | Start `uvicorn service:app --port 8001` in `tools/anpr` |
| Camera shows *ANPR not trained* | Train first: `models/plate.pt` and `models/chars.pt` are missing |

## Notes

- The models only know rendered plates. Before using a real camera, add real photos of plates
  (labelled with the same JSON, or in YOLO format) and train again.
- Generated images, `datasets/`, `runs/`, `models/` and `.venv/` are not committed (see `.gitignore`):
  every machine generates and trains its own, or copies `models/*.pt` from a machine that did.
- The 3D lanes replay vehicles that have already checked in or out, so the AI read is shown next to
  the recorded plate; it does not fill the check-in form yet.
