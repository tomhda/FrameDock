# silframe-cli

[English](CLI.md)

`silframe-cli.exe` は、SILframe のウィンドウを開かずに、動画の情報の取得、フレームの保存、切り出しを行うコマンドです。アプリと同じ処理を使うので、コマンドで書き出した動画は、編集パネルから書き出したものと同じになります。

スクリプトや AI エージェントから使うことを想定しています。どのコマンドも結果を 1 つの JSON で出力し、成否は終了コードで分かります。メッセージは常に英語です。

## 場所

セットアップが、アプリと同じフォルダーにインストールします。

```
%LOCALAPPDATA%\Programs\SILframe\silframe-cli.exe
```

どのフォルダーからでも `silframe-cli` という名前だけで実行するには、セットアップの追加タスクで、PATH に追加する項目を選びます。既定ではオフで、選ばない場合はフルパスで呼び出します。変更は、セットアップの完了後に開いたターミナルから有効になります。SILframe をアンインストールすると、追加した分は取り除かれます。

## コマンド

```
silframe-cli info <動画>
silframe-cli frame <動画> --time <時刻> [--out <ファイル.png>] [オプション]
silframe-cli export <動画> --out <ファイル.mp4|.mkv|.mov> [オプション]
silframe-cli --version
silframe-cli --help
```

時刻は、秒（`12.5`）、`m:ss`（`1:02.5`）、`h:mm:ss` のいずれかで、動画の先頭から数えます。

### info

長さ、映像のサイズ、フレームレート、コーデック、音声と字幕のトラック数を出力します。

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

実際の出力には、ほかの項目も含まれます。`displayWidth` と `displayHeight` は、回転を反映した、表示されるとおりの映像のサイズです。クロップの範囲はこのピクセルで指定します。

### frame

1 フレームを PNG で保存します。

| オプション | 意味 |
|---|---|
| `--time <時刻>` | フレームの位置。必須です。 |
| `--out <ファイル.png>` | 保存先。省略すると、動画と同じフォルダーに、動画名と時刻を付けた名前で保存します。 |
| `--crop X,Y,W,H` | 表示される映像の左上を原点とした、ピクセル単位の範囲。 |
| `--rotate 90\|180\|270` | 時計回りの回転。 |
| `--zoom F[,FX,FY]` | 注目点を中心に 1〜4 倍に拡大します。注目点は横と縦をそれぞれ 0〜1 で指定し、省略すると中央です。 |
| `--overwrite` | `--out` のファイルがすでにある場合に置き換えます。 |

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

動画を切り出して保存します。形式は `--out` の拡張子で決まり、MP4、MKV、MOV のいずれかです。再エンコードする場合、映像は H.264、音声は AAC です。

| オプション | 意味 |
|---|---|
| `--out <ファイル>` | 保存先。必須です。 |
| `--start <時刻>` | 省略すると 0。 |
| `--end <時刻>` | 省略すると動画の最後。 |
| `--segment 開始-終了` | 複数指定すると、指定した順につなげて 1 つのファイルにします。`--start` と `--end` の代わりに使います。 |
| `--crop`、`--rotate`、`--zoom` | `frame` と同じです。すべての区間に適用されます。 |
| `--speed <倍率>` | 0.5〜4。省略すると 1。 |
| `--copy` | 再エンコードせずに切り出します。速く、画質も変わりませんが、切り出し位置は近いキーフレームに移動します。クロップ、回転、ズーム、速度変更、複数区間とは併用できません。 |
| `--overwrite` | `--out` のファイルがすでにある場合に置き換えます。 |
| `--quiet` | 進捗を stderr に出しません。 |

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

`media` は書き出したファイルの情報で、項目は `info` と同じです。`--copy` を付けた場合、`mode` は `"copy"`、`boundariesAreApproximate` は `true` になります。

実行中は、`encoding 42%` のような進捗が stderr に出ます。stdout に出るのは最後の JSON だけです。

## 結果とエラー

| 終了コード | 意味 |
|---|---|
| 0 | 成功。stdout に `{"ok": true, ...}` が出ます。 |
| 1 | コマンドは解釈できたが、処理に失敗した。 |
| 2 | 使い方の誤り。コマンドやオプションが不明、値がない、値の形式が違う、など。 |

失敗したときの stdout は次の形です。

```json
{
  "ok": false,
  "error": {
    "code": "invalid_request",
    "message": "The end time must be after the start time."
  }
}
```

| `code` | 意味 |
|---|---|
| `usage` | コマンドラインの誤り。終了コードは 2 です。 |
| `invalid_request` | 指定どおりには実行できない。動画の範囲外の時刻、保存先のファイルがすでにある、など。 |
| `failed` | FFmpeg または ffprobe が失敗した。`detail` にその出力の末尾が入ります。 |
| `io_error` | ファイルを読み書きできなかった。 |
| `canceled` | Ctrl+C で中止した。途中まで書いたファイルは削除されます。 |

## 制限

アプリと同じ制限があります。ローカルファイルのみ、音声は最初のトラックのみ、ソフトウェアエンコード、HDR 動画は再エンコード不可（`--copy` を使います）。詳しくは [README](../README.ja.md#制限) を参照してください。
