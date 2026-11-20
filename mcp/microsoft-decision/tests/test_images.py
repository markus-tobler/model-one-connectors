import base64
import json
import struct
from pathlib import Path

import pytest

from microsoft_decision_mcp.images import contains_reference, inspect_image
from test_evaluate import call_evaluate, recording_transport

ASSETS = Path(__file__).resolve().parents[3] / "tests" / "assets"
CAT_PNG = ASSETS / "siamese-cat.png"
ASTRONAUT_JPG = ASSETS / "astronaut-eva.jpg"
NOUL = {"q": {"type": "noul", "instructions": "Does the image show a person?"}}


@pytest.fixture
def anyio_backend():
    return "asyncio"


def png(width, height, animated=False, pad_to=0):
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + b"\0\0\0\0"

    out = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0))
    if animated:
        out += chunk(b"acTL", bytes(8))
    out += chunk(b"IDAT", b"\x78\x9c\x03\x00\x00\x00\x00\x01")
    if pad_to:
        out += chunk(b"tEXt", bytes(max(0, pad_to - len(out) - 24)))
    return out + chunk(b"IEND", b"")


def jpeg(width, height, sof=0xC0):
    return (
        b"\xff\xd8"
        + b"\xff\xe0\x00\x10JFIF\x00\x01\x01\x00\x00\x01\x00\x01\x00\x00"
        + bytes([0xFF, sof, 0x00, 0x11, 0x08, height >> 8, height & 0xFF, width >> 8, width & 0xFF, 0x03])
        + b"\x01\x22\x00\x02\x11\x01\x03\x11\x01"
        + b"\xff\xd9"
    )


def b64(data):
    return base64.b64encode(data).decode()


async def evaluate(arguments):
    transport, sent = recording_transport()
    result = await call_evaluate(transport, {"questions": NOUL, **arguments})
    return result, [json.loads(request.content) for request in sent]


@pytest.mark.parametrize(("path", "expected"), [(CAT_PNG, ("png", 960, 763)), (ASTRONAUT_JPG, ("jpeg", 960, 960))])
def test_real_images_are_inspected_from_their_headers(path, expected):
    assert inspect_image(path.read_bytes(), "x") == expected


@pytest.mark.anyio
async def test_path_and_base64_images_become_native_assets_and_unreferenced_images_are_appended():
    result, [payload] = await evaluate(
        {
            "state": "Compare image:cat.png with the second picture.",
            "images": {
                "cat.png": {"path": str(CAT_PNG)},
                "astronaut.jpg": {"data_base64": b64(ASTRONAUT_JPG.read_bytes()), "format": "jpg"},
            },
        }
    )

    assert not result.is_error
    assert payload["state"] == "Compare image:cat.png with the second picture.\n\nimage:astronaut.jpg"
    assert payload["assets"] == {
        "cat.png": {"type": "image", "format": "png", "data_base64": b64(CAT_PNG.read_bytes())},
        "astronaut.jpg": {"type": "image", "format": "jpeg", "data_base64": b64(ASTRONAUT_JPG.read_bytes())},
    }


@pytest.mark.anyio
@pytest.mark.parametrize(
    "content",
    [
        lambda data: b64(data),
        lambda data: "data:image/png;base64," + b64(data),
        lambda data: "\n".join(b64(data)[i : i + 76] for i in range(0, len(b64(data)), 76)),
    ],
)
async def test_base64_content_forms_are_accepted(content):
    data = png(400, 300)

    result, [payload] = await evaluate({"state": "image:a", "images": {"a": {"data_base64": content(data)}}})

    assert not result.is_error
    assert payload["assets"]["a"]["data_base64"] == b64(data)


@pytest.mark.anyio
@pytest.mark.parametrize(
    ("state", "questions"),
    [
        ("Picture: image:a.", NOUL),
        ("s", {"q": {"type": "noul", "instructions": "Is image:a damaged?"}}),
        ("s", {"q": {"type": "noul", "criteria": {"true": "Looks like image:a"}}}),
        ("s", {"q": {"type": "choice", "criteria": {"first": "image:a", "second": None}}}),
        ("s", {"q": {"type": "score", "criteria": ["unlike image:a", "like image:a"]}}),
    ],
)
async def test_referenced_images_leave_state_unchanged(state, questions, tmp_path):
    image = tmp_path / "a.png"
    image.write_bytes(png(400, 400))

    result, [payload] = await evaluate({"state": state, "questions": questions, "images": {"a": {"path": str(image)}}})

    assert not result.is_error
    assert payload["state"] == state


@pytest.mark.anyio
@pytest.mark.parametrize("state", ["", "  ", None])
async def test_empty_state_becomes_the_image_references(state):
    images = {"a": {"data_base64": b64(png(400, 400))}, "b": {"data_base64": b64(jpeg(400, 400))}}

    result, [payload] = await evaluate({"state": state, "images": images})

    assert not result.is_error
    assert payload["state"] == "image:a image:b"


@pytest.mark.anyio
async def test_json_state_that_references_every_image_is_sent_unchanged():
    state = {"photo": "image:a", "count": 3}

    result, [payload] = await evaluate({"state": state, "images": {"a": {"data_base64": b64(png(400, 400))}}})

    assert not result.is_error
    assert payload["state"] == state


@pytest.mark.anyio
async def test_json_state_with_an_unreferenced_image_is_rejected():
    result, sent = await evaluate({"state": {"message": "Checkout is down"}, "images": {"a": {"data_base64": b64(png(400, 400))}}})

    assert result.is_error
    assert "image:a" in result.content[0].text
    assert sent == []


@pytest.mark.anyio
async def test_no_images_send_no_assets():
    for images in (None, {}):
        arguments = {"state": "text"} if images is None else {"state": "text", "images": images}
        result, [payload] = await evaluate(arguments)
        assert not result.is_error
        assert "assets" not in payload
        assert payload["state"] == "text"


@pytest.mark.anyio
@pytest.mark.parametrize(("width", "height"), [(280, 280), (1568, 1568), (280, 1568)])
async def test_boundary_dimensions_are_accepted(width, height):
    images = {"p": {"data_base64": b64(png(width, height))}, "j": {"data_base64": b64(jpeg(width, height))}}

    result, [payload] = await evaluate({"state": "image:p image:j", "images": images})

    assert not result.is_error
    assert len(payload["assets"]) == 2


@pytest.mark.anyio
async def test_progressive_jpeg_and_exact_16_mib_are_accepted():
    images = {"j": {"data_base64": b64(jpeg(800, 600, sof=0xC2))}, "p": {"data_base64": b64(png(400, 400, pad_to=16 * 1024 * 1024))}}

    result, [payload] = await evaluate({"state": "image:j image:p", "images": images})

    assert not result.is_error
    assert payload["assets"]["j"]["format"] == "jpeg"


def _invalid_images(tmp):
    big = tmp / "big.png"
    big.write_bytes(png(400, 400, pad_to=16 * 1024 * 1024 + 1))
    ok = b64(png(400, 400))
    return [
        ({"my photo": {"data_base64": ok}}, "pattern"),
        ({"a" * 65: {"data_base64": ok}}, "at most 64"),
        ({"a": {"data_base64": ok, "path": "x"}}, "Extra inputs"),
        ({"a": {"data_base64": "not base64!"}}, "not valid base64"),
        ({"a": {"data_base64": "data:image/png," + ok}}, "not base64 encoded"),
        ({"a": {"data_base64": ""}}, "no content"),
        ({"a": {"data_base64": b64(b"GIF89a\x90\x01\x90\x01\0\0\0")}}, "must be a PNG or JPEG"),
        ({"a": {"data_base64": b64(b"RIFF\x10\0\0\0WEBPVP8 \0\0\0\0")}}, "must be a PNG or JPEG"),
        ({"a": {"data_base64": ok, "format": "gif"}}, "format"),
        ({"a": {"data_base64": ok, "format": "jpeg"}}, "is PNG, not JPEG"),
        ({"a": {"data_base64": b64(png(400, 400, animated=True))}}, "animated"),
        ({"a": {"data_base64": b64(png(400, 400)[:20])}}, "could not be read"),
        ({"a": {"data_base64": b64(b"\xff\xd8\xff\xd9")}}, "could not be read"),
        ({"a": {"data_base64": b64(png(279, 600))}}, "279x600"),
        ({"a": {"data_base64": b64(png(600, 1569))}}, "600x1569"),
        ({"a": {"data_base64": b64(jpeg(1569, 1569))}}, "1569x1569"),
        ({"a": {"path": str(big)}}, "larger than 16 MiB"),
        ({"a": {"data_base64": b64(png(400, 400, pad_to=16 * 1024 * 1024 + 1))}}, "larger than 16 MiB"),
        ({"a": {"path": str(tmp / "missing.png")}}, "Cannot read image 'a'"),
    ]


@pytest.mark.anyio
async def test_invalid_images_are_rejected_without_a_call(tmp_path):
    for images, message in _invalid_images(tmp_path):
        result, sent = await evaluate({"state": "image:a", "images": images})

        assert result.is_error, images
        assert message in result.content[0].text, (message, result.content[0].text)
        assert sent == []


@pytest.mark.parametrize(
    ("text", "name", "expected"),
    [
        ("image:photo.png", "photo.png", True),
        ("See image:photo.png.", "photo.png", True),
        ("See image:photo.png.\nNext", "photo.png", True),
        ("image:photo.pngx", "photo.png", False),
        ("image:photo.png-2", "photo.png", False),
        ("image:photo.png.bak", "photo.png", False),
        ("image:a.b", "a", False),
        ("image:ab image:a", "a", True),
        ("image: a", "a", False),
        ("Image:a", "a", False),
    ],
)
def test_image_references_need_a_name_boundary(text, name, expected):
    assert contains_reference(text, name) is expected
