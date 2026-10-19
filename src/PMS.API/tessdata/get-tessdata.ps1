# Downloads the Tesseract language data used to OCR vehicle papers (cà vẹt, CCCD) into this folder.
# Run once per machine: powershell -ExecutionPolicy Bypass -File src/PMS.API/tessdata/get-tessdata.ps1
# "fast" models (~1-4 MB each) from the official tesseract-ocr/tessdata_fast repository; switch the
# base URL to tessdata_best for slightly better accuracy at ~10x the size.
$ErrorActionPreference = 'Stop'
$base = 'https://github.com/tesseract-ocr/tessdata_fast/raw/main'
foreach ($language in 'vie', 'eng') {
    $target = Join-Path $PSScriptRoot "$language.traineddata"
    if (Test-Path $target) { Write-Host "$language.traineddata already present"; continue }
    Write-Host "Downloading $language.traineddata..."
    Invoke-WebRequest -Uri "$base/$language.traineddata" -OutFile $target -UseBasicParsing
}
