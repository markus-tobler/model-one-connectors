"""Image assets for the decision API: validation, loading, and image:<name> references.

The rules and limits match the connector's custom code; keep both in sync.
"""

from __future__ import annotations

import base64
import binascii
import re
import struct
from pathlib import Path
from typing import Annotated, Any, Literal, Union

from pydantic import BaseModel, ConfigDict, Field

MIN_IMAGE_SIDE = 280
MAX_IMAGE_SIDE = 1568
MAX_IMAGE_BYTES = 16 * 1024 * 1024
MAX_IMAGE_NAME_LENGTH = 64
REFERENCE_PREFIX = "image:"

ImageName = Annotated[
    str,
    Field(
        min_length=1,
        max_length=MAX_IMAGE_NAME_LENGTH,
        pattern=r"^[A-Za-z0-9._-]+$",
        description="Image name used in image:<name> references. Letters, digits, '.', '_', and '-' only.",
    ),
]

ImageFormat = Literal["png", "jpeg", "jpg"]


class ImageError(ValueError):
    pass


class PathImage(BaseModel):
    """A local PNG or JPEG file."""

    model_config = ConfigDict(extra="forbid")

    path: str = Field(description="Path of a local PNG or JPEG file. Prefer absolute paths; relative paths resolve against the server's working directory.")
    format: ImageFormat | None = Field(None, description="Optional. Detected from the file content when omitted.")


class Base64Image(BaseModel):
    """Base64-encoded PNG or JPEG data."""

    model_config = ConfigDict(extra="forbid")

    data_base64: str = Field(description="Base64-encoded PNG or JPEG data. A data: URI is also accepted.")
    format: ImageFormat | None = Field(None, description="Optional. Detected from the content when omitted.")


Image = Union[PathImage, Base64Image]

_NAME_CHARS = frozenset("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789._-")
_PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"


def build_assets(images: dict[str, Image]) -> dict[str, dict[str, str]]:
    """Validates every image and returns native decision-API assets keyed by name."""
    return {name: _build_asset(name, image) for name, image in images.items()}


def _build_asset(name: str, image: Image) -> dict[str, str]:
    data = _load_path(name, image.path) if isinstance(image, PathImage) else _decode_base64(name, image.data_base64)
    detected, width, height = inspect_image(data, name)

    if image.format is not None:
        requested = "jpeg" if image.format == "jpg" else image.format
        if requested != detected:
            raise ImageError(f"Image '{name}' is {detected.upper()}, not {requested.upper()}.")

    if not (MIN_IMAGE_SIDE <= width <= MAX_IMAGE_SIDE and MIN_IMAGE_SIDE <= height <= MAX_IMAGE_SIDE):
        raise ImageError(
            f"Image '{name}' is {width}x{height} pixels; each side must be between {MIN_IMAGE_SIDE} and {MAX_IMAGE_SIDE} pixels."
        )

    return {"type": "image", "format": detected, "data_base64": base64.b64encode(data).decode("ascii")}


def _load_path(name: str, path: str) -> bytes:
    file = Path(path).expanduser()
    try:
        size = file.stat().st_size
        if size > MAX_IMAGE_BYTES:
            raise ImageError(f"Image '{name}' is larger than 16 MiB.")
        return file.read_bytes()
    except OSError as ex:
        raise ImageError(f"Cannot read image '{name}' from {file}: {ex.strerror or ex}.") from ex


def _decode_base64(name: str, text: str) -> bytes:
    text = text.strip()
    if text[:5].lower() == "data:":
        header, comma, rest = text.partition(",")
        if not comma or not header.lower().endswith(";base64"):
            raise ImageError(f"Image '{name}' is a data URI that is not base64 encoded.")
        text = rest
    if not text:
        raise ImageError(f"Image '{name}' has no content.")
    # Reject oversized input before decoding it; whitespace is allowed, hence the slack.
    if len(text) > 2 * ((MAX_IMAGE_BYTES + 2) // 3) * 4:
        raise ImageError(f"Image '{name}' is larger than 16 MiB.")
    try:
        data = base64.b64decode(re.sub(r"\s+", "", text), validate=True)
    except (binascii.Error, ValueError) as ex:
        raise ImageError(f"Image '{name}' is not valid base64.") from ex
    if len(data) > MAX_IMAGE_BYTES:
        raise ImageError(f"Image '{name}' is larger than 16 MiB.")
    return data


def inspect_image(data: bytes, name: str) -> tuple[str, int, int]:
    """Returns the format, width, and height from the PNG or JPEG header without decoding pixels."""
    if data.startswith(_PNG_SIGNATURE):
        return ("png", *_inspect_png(data, name))
    if data.startswith(b"\xff\xd8\xff"):
        return ("jpeg", *_inspect_jpeg(data, name))
    raise ImageError(f"Image '{name}' must be a PNG or JPEG.")


def _inspect_png(data: bytes, name: str) -> tuple[int, int]:
    if len(data) < 33 or data[12:16] != b"IHDR":
        raise ImageError(f"The header of image '{name}' could not be read.")
    width, height = struct.unpack(">II", data[16:24])

    # An acTL chunk before the first IDAT marks an animated PNG.
    offset = 8
    while offset + 8 <= len(data):
        (length,) = struct.unpack(">I", data[offset : offset + 4])
        chunk = data[offset + 4 : offset + 8]
        if chunk == b"acTL":
            raise ImageError(f"Image '{name}' is animated; use a still PNG or JPEG.")
        if chunk in (b"IDAT", b"IEND"):
            break
        offset += 12 + length
    return width, height


def _inspect_jpeg(data: bytes, name: str) -> tuple[int, int]:
    position = 2
    while position < len(data):
        if data[position] != 0xFF:
            break
        while position < len(data) and data[position] == 0xFF:
            position += 1
        if position >= len(data):
            break
        marker = data[position]
        if marker == 0x01 or 0xD0 <= marker <= 0xD7:
            position += 1
            continue
        if marker in (0xD9, 0xDA) or position + 2 >= len(data):
            break
        length = (data[position + 1] << 8) | data[position + 2]
        if length < 2:
            break
        if 0xC0 <= marker <= 0xCF and marker not in (0xC4, 0xC8, 0xCC):
            if length < 7 or position + 7 >= len(data):
                break
            height = (data[position + 4] << 8) | data[position + 5]
            width = (data[position + 6] << 8) | data[position + 7]
            return width, height
        position += 1 + length
    raise ImageError(f"The header of image '{name}' could not be read.")


def contains_reference(text: str, name: str) -> bool:
    """image:<name> counts when the name is not followed by another name character; a trailing period counts."""
    token = REFERENCE_PREFIX + name
    index = text.find(token)
    while index >= 0:
        end = index + len(token)
        if (
            end == len(text)
            or text[end] not in _NAME_CHARS
            or (text[end] == "." and (end + 1 == len(text) or text[end + 1] not in _NAME_CHARS))
        ):
            return True
        index = text.find(token, index + 1)
    return False


def reference_images(state: Any, questions: Any, names: list[str]) -> Any:
    """Appends image:<name> to text state for every image that no text references.

    The decision API rejects requests that reference only some of their images. JSON state is never changed.
    """
    texts = list(_strings(state)) + list(_strings(questions))
    missing = [name for name in names if not any(contains_reference(text, name) for text in texts)]
    if not missing:
        return state
    if isinstance(state, (dict, list)):
        raise ImageError(
            f"Reference image '{missing[0]}' as image:{missing[0]} in the state, the instructions, or the criteria. "
            "JSON state is not changed automatically."
        )
    references = " ".join(REFERENCE_PREFIX + name for name in missing)
    text = state if isinstance(state, str) else ""
    return references if not text.strip() else f"{text}\n\n{references}"


def _strings(value: Any):
    if isinstance(value, str):
        yield value
    elif isinstance(value, dict):
        for item in value.values():
            yield from _strings(item)
    elif isinstance(value, list):
        for item in value:
            yield from _strings(item)
