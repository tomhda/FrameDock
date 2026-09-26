# FrameDock

FrameDock は Windows 向けの動画プレイヤーです。動画を開いて再生し、同じウィンドウから範囲指定、クロップ、MP4 書き出しを行えます。

## 必要な環境

- Windows 10 バージョン 2004（ビルド 19041）以降、または Windows 11 x64
- .NET 8 SDK
- 初回の依存取得時はインターネット接続と、Windows 11 に含まれる `tar.exe`
- PowerShell 5.1 以降

## Release の作成と実行

リポジトリのルートで PowerShell を開き、次を実行します。スクリプトは依存アーカイブとライセンス文書の SHA-256 を検証してから配置します。実行ポリシーの変更が必要な環境では、次のように現在のプロセスだけに指定できます。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\build-release.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\run.ps1
```

Release は `artifacts\release\win-x64` に生成されます。スクリプトは Release 中間生成物を clean してから発行し、WinUI の `App.xbf`、`MainWindow.xbf`、`FrameDock.pri` を含むことを確認します。`FrameDock.exe` と同じ場所に `libmpv-2.dll`、`Media\ffmpeg.exe`、`Media\ffprobe.exe` が配置され、`ThirdPartyNotices` にライセンスとビルド情報が入ります。インストーラーや Microsoft Store パッケージは作成しません。

依存物は `vendor\`、ダウンロードしたアーカイブと一時ファイルは `tools\.cache\` に置かれます。これらはブートストラップが管理する生成物です。別の配置先で動作確認する場合は、`-InstallRoot` と `-CacheRoot` を指定できます。ローカルに保管したアーカイブを使う場合は `-MpvArchivePath` と `-FfmpegArchivePath` を渡せますが、固定 SHA-256 に合わないファイルは拒否されます。

```powershell
$bootstrapScript = Join-Path (Join-Path $PWD 'tools') 'bootstrap-dependencies.ps1'
& $bootstrapScript -InstallRoot 'D:/FrameDock-stage' -CacheRoot 'D:/FrameDock-cache' -MpvArchivePath 'D:/archives/mpv-dev-lgpl-x86_64-20260925-git-35af06172b.7z' -FfmpegArchivePath 'D:/archives/ffmpeg-9.0.2-essentials_build.7z'
```

`build-release.ps1` は既定で依存を再検証します。すでに取得済みでネット接続がない場合は `-SkipDependencyBootstrap` を指定できます。その場合も `vendor\` 内の全バイナリとライセンス文書の SHA-256 を照合します。出力先を変えるときは、リポジトリ内のパスを `-OutputDirectory` に指定してください。

## 使い方

1. 「開く」ボタンまたはドラッグ＆ドロップでローカル動画を開きます。
2. 「トリム／クロップ」を選び、開始・終了時刻を秒で入力するか、「現在位置」で再生位置を設定します。
3. 必要なら「クロップ範囲を選択」を押し、表示中の映像上をドラッグします。矩形は回転補正後の正方ピクセル表示を基準に扱います。
4. 保存先と出力名を決め、「MP4 に書き出す」を押します。既定は CPU による正確な再エンコードです。進行状況の表示中はキャンセルできます。

「高速コピー」を選ぶと、映像と音声を再エンコードせずにコピーします。これはキーフレームの位置で切れるため、指定した秒と前後することがあります。クロップとは併用できません。

## 制限

- 入力と出力はローカルファイルを対象にし、出力形式は MP4 です。
- 正確な書き出しはソフトウェア `libx264` を使います。GPU エンコーダーは必要ありません。
- 正確な HDR 再エンコードは、色調を誤って変換しないよう現在は拒否します。HDR はクロップなしの高速コピーであればメタデータを保ったまま出力できます。
- 4:2:0 H.264 のクロップ範囲は、回転補正後の表示座標で指定し、位置と大きさを偶数ピクセルにそろえます。クロップなしの奇数サイズ映像は右端または下端に最大 1 ピクセルを追加します。
- 出力先に同名ファイルがある場合は上書きせず、元動画を出力先に指定することもできません。

## ビルドとテスト

Core ライブラリのビルドと、FFmpeg を使わない幾何チェック:

```powershell
dotnet build .\FrameDock.Core\FrameDock.Core.csproj
dotnet run --project .\FrameDock.Core.Tests\FrameDock.Core.Tests.csproj
```

同梱 FFmpeg でエンコード・ffprobe 統合チェックも実行するには、次の環境変数を設定してからテストハーネスを起動します。

```powershell
$env:FRAMEDOCK_TEST_FFMPEG = (Resolve-Path .\vendor\ffmpeg\ffmpeg.exe).Path
$env:FRAMEDOCK_TEST_FFPROBE = (Resolve-Path .\vendor\ffmpeg\ffprobe.exe).Path
dotnet run --project .\FrameDock.Core.Tests\FrameDock.Core.Tests.csproj
```

両方の環境変数が設定されていない場合、外部ツールを使う統合チェックは明示的に `SKIP` と表示されます。テスト項目と Core の座標・時間契約は [`EXPORT-NOTES.md`](EXPORT-NOTES.md) に記載しています。

## ライセンスと依存関係

FFmpeg の同梱ビルドは GPLv3 構成です。mpv DLL は LGPLv2.1 以降のビルドです。配布元、ソース、SHA-256、同梱するライセンス文書は [`THIRD-PARTY-LICENSES.md`](THIRD-PARTY-LICENSES.md) を参照してください。FrameDock 全体の配布ライセンスはまだ宣言していません。Release を他者に配布する前に、GPLv3 の条件を含む各コンポーネントの条件を満たす配布形態を決めてください。
