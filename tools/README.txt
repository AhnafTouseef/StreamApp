StreamApp keeps FFmpeg here so the whole project folder can be copied to another computer.

Put these two files in THIS folder:
    Windows:  ffmpeg.exe  and  ffprobe.exe
    Linux/Mac: ffmpeg  and  ffprobe

Easiest way: run StreamApp, sign in, open Admin > Dashboard and press
"Download FFmpeg into the project". It does the rest.

By hand: download the "release essentials" zip from https://www.gyan.dev/ffmpeg/builds/,
open its "bin" folder and copy ffmpeg.exe and ffprobe.exe here. Then press "Check again" on the
Admin dashboard (no restart needed).

Lookup order: Media:FfmpegPath / Media:FfprobePath if you set them -> this folder -> PATH -> usual install folders.
Leave those two settings empty to use this folder.
