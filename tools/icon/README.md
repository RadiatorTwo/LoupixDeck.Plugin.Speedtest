# Plugin icon generator

`make_icon.py` draws the plugin icon: the Material Design Icons `speedometer` glyph
(Pictogrammers, Apache-2.0) on a night-blue tile, with a teal-to-lime gradient arc, a white
needle and download / upload arrows.

```bash
pip install pillow numpy
python tools/icon/make_icon.py tools/icon/out
cp tools/icon/out/icon_256.png icon.png
```

The script writes `icon_{256,128,64,32,16}.png` into the given folder; only the 256 px file is
used, as `icon.png` in the repo root. The `out/` folder is not committed.
