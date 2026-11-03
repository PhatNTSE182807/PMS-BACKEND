# Plate reader (ANPR) tools

Train a Vietnamese number plate reader on images rendered from the 3D gate lanes, then read
plates in a picture. Two YOLO detectors work one after the other:

1. **Plate detector:** finds the plate in a full camera frame.
2. **Character detector:** finds each character in the plate crop (30 classes: `0-9` and the
   series letters). Characters are put in rows (motorbike plates have two) and read left to right,
   then formatted as printed: `51K-889.24`, `59X1-234.56`.

The confidence of a read is the plate confidence times the mean character confidence. The gate
asks the Staff to confirm plates read below 0.85 (`Parking:Gate:AnprConfidenceThreshold`).

## 1. Make training images

Run the frontend in development (`npm run dev`), sign in as Admin and open
`/admin/anpr-dataset` (**Plate reader training images**, development builds only). Use Chrome or
Edge on a computer.

- **Preview a sample** draws one frame with its labels: plate box in green, character boxes in amber.
- **Choose folder and generate** saves `images/*.jpg` (1280 × 720) and `labels/*.json` into the
  folder you pick. Each label file has the plate text, the vehicle kind, the plate box and every
  character box (0..1 from the top-left corner).

Every frame has a random vehicle (car or motorbike, set the share), plate, position, angle,
camera pose and zoom, brightness, barrier position and plate dirt. 3,000 to 5,000 images train
well; make a second, smaller folder (300 images) to test on.

## 2. Install (Python 3.10 to 3.12)

```bash
cd tools/anpr
python -m venv .venv
.venv\Scripts\activate          # macOS / Linux: source .venv/bin/activate
pip install -r requirements.txt
```

For an NVIDIA GPU, install the CUDA build of PyTorch first (see pytorch.org), then the requirements.
Training also runs on the CPU, only slower.

## 3. Build the datasets

```bash
python prepare_dataset.py --src D:/anpr/generated --out datasets
```

`--src` takes several folders. This writes `datasets/plate.yaml` (full frames, one class) and
`datasets/chars.yaml` (plate crops, one class per character), each split into train and val.

## 4. Train

```bash
python train.py --data datasets --epochs 60
```

Starts from `yolo11n.pt` (downloaded by ultralytics) and saves `models/plate.pt` and
`models/chars.pt`. Training runs and charts are in `runs/`. `--only plate` or `--only chars`
trains one detector; `--device cpu` forces the CPU.

## 5. Read and measure

```bash
python read_plate.py some-frame.jpg --save read.jpg
python evaluate.py --src D:/anpr/test-set
```

`read_plate.py` prints every plate found (text, confidence, box). `evaluate.py` reports how many
plates of a labelled folder were read exactly and how many reads above the threshold were wrong.

## Notes

- The models only know rendered plates. Before using a real camera, add real photos of plates
  (labelled with the same JSON, or in YOLO format) and train again.
- Generated data, `datasets/`, `runs/` and `models/` are not committed (see `.gitignore`).
- Next step: a small service that takes camera frames, runs `PlateReader` and fills the plate and
  `AnprConfidence` of the gate check-in.
