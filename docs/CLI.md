# silframe-cli

[日本語](CLI.ja.md)

`silframe-cli.exe` inspects videos, saves frames, and exports clips without opening the SILframe window. It uses the same code as the app, so a clip exported from the command line is the same as one exported from the editor.

It is made for scripts and AI agents: every command prints one JSON object, the exit code says whether it worked, and messages are always in English.

## Where it is

The setup installs it next to the app:

```
%LOCALAPPDATA%\Programs\SILframe\silframe-cli.exe
```

To start it as `silframe-cli` from any folder, select the optional task that adds the SILframe folder to PATH in the setup. The option is off by default; without it, call the tool by its full path. The change applies to terminals opened after the setup finishes, and uninstalling SILframe removes the entry.

## Commands

```
silframe-cli info <video>
silframe-cli frame <video> --time <time> [--out <file.png>] [options]
silframe-cli export <video> --out <file.mp4|.mkv|.mov> [options]
silframe-cli --version
silframe-cli --help
```

Times are seconds (`12.5`), `m:ss` (`1:02.5`), or `h:mm:ss`, measured from the start of the video.

### info

Prints the duration, picture size, frame rate, codecs, and the number of audio and subtitle tracks.

```
silframe-cli info talk.mp4
```

```json
{
  "ok": true,
  "media": {
    "sourcePath": "C:\\videos\\talk.mp4",
    "durationSeconds": 734.2,
    "displayWidth": 1920,
    "displayHeight": 1080,
    "frameRate": 29.97,
    "videoCodec": "h264",
    "audioCodec": "aac",
    "hasAudio": true,
    "audioStreamCount": 1,
    "subtitleStreamCount": 0
  }
}
```

The real output has more fields. `displayWidth` and `displayHeight` describe the picture as it is shown, after rotation. Crop rectangles use these pixels.

### frame

Saves one frame as a PNG.

| Option | Meaning |
|---|---|
| `--time <time>` | Position of the frame. Required. |
| `--out <file.png>` | Where to save. Default: next to the video, named after the video and the time. |
| `--crop X,Y,W,H` | Rectangle in pixels of the displayed picture, from the top-left corner. |
| `--rotate 90\|180\|270` | Clockwise rotation. |
| `--zoom F[,FX,FY]` | Magnify 1 to 4 times around a focus point. The focus is 0 to 1 across and down; the default is the center. |
| `--overwrite` | Replace `--out` if it exists. |

```
silframe-cli frame talk.mp4 --time 1:23.5 --out slide.png
```

```json
{
  "ok": true,
  "output": "C:\\videos\\slide.png",
  "timeSeconds": 83.5
}
```

### export

Writes a clip. The extension of `--out` selects MP4, MKV, or MOV. Re-encoded output is H.264 video with AAC audio.

| Option | Meaning |
|---|---|
| `--out <file>` | Where to save. Required. |
| `--start <time>` | Default: 0. |
| `--end <time>` | Default: the end of the video. |
| `--segment START-END` | Repeat to join several ranges into one file, in the order given. Replaces `--start` and `--end`. |
| `--crop`, `--rotate`, `--zoom` | As for `frame`. Applied to every segment. |
| `--speed <factor>` | 0.5 to 4. Default: 1. |
| `--copy` | Cut without re-encoding. Fast and lossless, but the cut points move to nearby keyframes. Cannot be combined with crop, rotate, zoom, speed, or several segments. |
| `--overwrite` | Replace `--out` if it exists. |
| `--quiet` | No progress on stderr. |

```
silframe-cli export talk.mp4 --start 0:10 --end 0:40 --out clip.mp4
silframe-cli export talk.mp4 --segment 0:10-0:20 --segment 1:00-1:15 --out highlights.mp4
silframe-cli export talk.mp4 --start 5:00 --end 9:30 --copy --out part.mkv
```

```json
{
  "ok": true,
  "output": "C:\\videos\\clip.mp4",
  "mode": "reencode",
  "boundariesAreApproximate": false,
  "requestedDurationSeconds": 30,
  "media": { "durationSeconds": 30.0, "displayWidth": 1920, "displayHeight": 1080 }
}
```

`media` describes the file that was written, with the same fields as `info`. With `--copy`, `mode` is `"copy"` and `boundariesAreApproximate` is `true`.

While it runs, progress lines such as `encoding 42%` go to stderr. Stdout carries only the final JSON.

## Results and errors

| Exit code | Meaning |
|---|---|
| 0 | Success. Stdout has `{"ok": true, ...}`. |
| 1 | The command was understood but failed. |
| 2 | Wrong usage: unknown command or option, missing or malformed value. |

On failure stdout has:

```json
{
  "ok": false,
  "error": {
    "code": "invalid_request",
    "message": "The end time must be after the start time."
  }
}
```

| `code` | Meaning |
|---|---|
| `usage` | The command line is wrong. Exit code 2. |
| `invalid_request` | The request cannot be done as asked, for example a range outside the video or an output file that already exists. |
| `failed` | FFmpeg or ffprobe failed. `detail` has the end of its output. |
| `io_error` | A file could not be read or written. |
| `canceled` | Stopped with Ctrl+C. Partial output is removed. |

## Limits

The limits of the app apply: local files only, the first audio track only, software encoding, and no re-encoding of HDR video (use `--copy` for those). See the [README](../README.md#limits).
