# Sticker — Python prototype

The original prototype (rembg + PyQt6), kept as a test bench. It shares the
model folder (`~/.u2net`), matte cache, and `session.json` with the C# app,
so sessions and mattes are interchangeable between the two.

## Setup

```
pip install "rembg[cpu]" PyQt6 pillow onnxruntime
```

## Use

```
python sticker.py photo.jpg
python sticker.py --no-matte logo.png   # already transparent
python sticker.py --restore             # reopen last session
python sticker.py --model birefnet-general x.jpg
```

Controls match the C# app (see the main README) minus pin mode and
paste-as-sticker. Mattes run on CPU here (`rembg[gpu]` for GPU); the matting
quality is identical to the C# app since both run the same ONNX models.
