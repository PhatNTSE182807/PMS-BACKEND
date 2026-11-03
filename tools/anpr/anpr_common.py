"""Shared constants and helpers for the plate reader tools."""

import re

# Characters printed on Vietnamese plates: digits and the series letters (I, J, O, Q, R, W are not used).
# The order is the class id of the character detector; keep it in sync with the trained model.
CHAR_CLASSES = list("0123456789ABCDEFGHKLMNPSTUVXYZ")
CHAR_INDEX = {c: i for i, c in enumerate(CHAR_CLASSES)}

# Margin added around the plate box before reading the characters (share of the plate size).
CROP_MARGIN = 0.08

# Image sizes the two detectors are trained and run at.
PLATE_IMGSZ = 640
CHARS_IMGSZ = 320

# Look-alike characters, fixed by position: digits where a digit must be, letters where a letter must be.
_TO_DIGIT = {"B": "8", "D": "0", "G": "6", "S": "5", "Z": "2", "T": "7", "A": "4", "L": "1"}
_TO_LETTER = {"8": "B", "0": "D", "6": "G", "5": "S", "2": "Z", "7": "T", "4": "A", "1": "L"}


def _digits(text: str) -> str:
    return "".join(_TO_DIGIT.get(c, c) for c in text)


def _letter(c: str) -> str:
    return _TO_LETTER.get(c, c)


def format_plate(rows: list[str]) -> str:
    """Turns the character rows read on a plate into the printed form ("51K-889.24", "59X1-234.56").

    One row: a car plate (province, one or two letters, four or five digits).
    Two rows: a motorbike plate (province and series on top, the number below).
    Text that does not look like a plate is returned as read.
    """
    rows = [r for r in rows if r]
    if not rows:
        return ""
    if len(rows) >= 2:
        top, number = rows[0], _digits("".join(rows[1:]))
        if len(top) < 3:
            return top + number
        province = _digits(top[:2])
        series = _letter(top[2]) + top[3:]
        if len(series) == 2 and not series[1].isalpha():
            series = series[0] + _digits(series[1])
    else:
        text = rows[0]
        match = re.match(r"^(.{2})([A-Z0-9]{1,2}?)(.{4,5})$", text)
        if not match:
            return text
        province = _digits(match.group(1))
        series = "".join(_letter(c) for c in match.group(2))
        number = _digits(match.group(3))
    printed = f"{number[:3]}.{number[3:]}" if len(number) == 5 else number
    return f"{province}{series}-{printed}"


def normalise(plate: str) -> str:
    """Letters and digits only, for comparing plates ("51K-889.24" -> "51K88924")."""
    return re.sub(r"[^0-9A-Z]", "", plate.upper())
