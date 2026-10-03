# FrameDock

FrameDock は Windows 向けの動画プレイヤーです。動画を開いて再生し、同じウィンドウで範囲指定、クロップ、回転、速度変更をして、MP4 / MKV / MOV に書き出せます。

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

Release は `artifacts\release\win-x64` に生成されます。スクリプトは Release 中間生成物を clean してから発行し、WinUI の `App.xbf`、`MainWindow.xbf`、`FrameDock.pri` を含むことを確認します。`FrameDock.exe` と同じ場所に `libmpv-2.dll`、`Media\ffmpeg.exe`、`Media\ffprobe.exe` が配置され、`ThirdPartyNotices` にライセンスとビルド情報が入ります。

## Windows インストーラー

Inno Setup はビルド時だけ使用し、インストール先には同梱しません。商用利用時は [Inno Setup のライセンス案内](https://jrsoftware.org/isorder.php) を確認してください。

Windows x64 で次を実行すると、Release を作成して一つのオフライン対応セットアップ EXE をビルドします。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\build-installer.ps1
```

出力先は `artifacts\installer\FrameDock-Setup-<バージョン>-win-x64.exe` です（現在のアプリバージョンは `1.0.0`）。初回のビルドは固定バージョンの Inno Setup 7.1.0 を公式 GitHub リリースから取得し、SHA-256 と Authenticode 署名を確認して `tools\.cache\` 内だけに展開します。セットアップには Release のアプリ、WinUI リソース、libmpv、FFmpeg、ライセンス通知が入るため、インストール先で追加ダウンロードはありません。既存 Release を使う場合は `-SkipReleaseBuild` を指定できます。

セットアップはユーザーごとに `%LOCALAPPDATA%\Programs\FrameDock` へインストールし、スタートメニューの起動項目とアンインストーラーを作成します。デスクトップアイコンと一般的な動画形式の「プログラムから開く」登録は任意です。Windows の既定アプリは変更しません。セットアップは未署名のため、Windows SmartScreen が警告を表示する場合があります。

依存物は `vendor\`、ダウンロードしたアーカイブと一時ファイルは `tools\.cache\` に置かれます。これらはブートストラップが管理する生成物です。別の配置先で動作確認する場合は、`-InstallRoot` と `-CacheRoot` を指定できます。ローカルに保管したアーカイブを使う場合は `-MpvArchivePath` と `-FfmpegArchivePath` を渡せますが、固定 SHA-256 に合わないファイルは拒否されます。

```powershell
$bootstrapScript = Join-Path (Join-Path $PWD 'tools') 'bootstrap-dependencies.ps1'
& $bootstrapScript -InstallRoot 'D:/FrameDock-stage' -CacheRoot 'D:/FrameDock-cache' -MpvArchivePath 'D:/archives/mpv-dev-lgpl-x86_64-20260925-git-35af06172b.7z' -FfmpegArchivePath 'D:/archives/ffmpeg-9.0.2-essentials_build.7z'
```

`build-release.ps1` は既定で依存を再検証します。すでに取得済みでネット接続がない場合は `-SkipDependencyBootstrap` を指定できます。その場合も `vendor\` 内の全バイナリとライセンス文書の SHA-256 を照合します。出力先を変えるときは、リポジトリ内のパスを `-OutputDirectory` に指定してください。

## 使い方

1. 「動画を開く」ボタンまたはドラッグ＆ドロップでローカル動画を開きます。
2. 再生、シーク、フレーム送り、音量は下部の操作列から使えます。右端の「その他の操作」→「表示モード」で「常時表示」「最大化（タスクバー表示）」「全画面表示」を切り替えられます。速度と再生設定も「その他の操作」にあります。
3. カメラボタンは表示中のフレームを PNG で保存します。保存先は「その他の操作」→「設定…」で、`ピクチャ\FrameDock`（既定）、指定したフォルダー、動画と同じフォルダー、動画と同じ場所のサブフォルダーから選べます。選んだ場所に保存できない場合は `ピクチャ\FrameDock` に保存します。ファイル名には動画名と再生時刻が入り、同名画像がある場合は番号を付けて残します。保存すると映像の上に通知が出て、「フォルダーを開く」で保存した場所を開けます。動画上で右クリックすると、現在のフレームをコピーできます。
4. 「その他の操作」から「編集…」を開きます。再生位置を合わせて `[` のボタン（I キー）で開始点、`]` のボタン（O キー）で終了点を設定します。タイムライン上の範囲をドラッグするか、「開始」「終了」に `0:12.5` の形式または秒数を入力しても調整できます。
5. 必要なら「クロップ」を押し、四隅と四辺の 8 つのハンドルで範囲を調整して「完了」を押します。「解除」で画面全体に戻ります。範囲は回転補正後の正方ピクセル表示を基準に扱います。回転ボタンは時計回りに 90 度ずつ回転します。
6. ズームは 1×〜4× で調整でき、拡大中は映像をドラッグして表示位置を移動できます。倍率と位置は書き出しにも反映されます。「速度」ではプレビューと書き出しの速度を 0.5×〜4× から選べます。通常再生の速度とは別に設定します。「リセット」は編集内容を最初の状態に戻し、「×」は編集内容を残したまま編集を閉じます。新しい動画を開くと編集内容は初期化されます。
7. ハサミのボタン（S キー）は再生位置で区間を分割します。クロップ、回転、ズーム、速度は、再生位置がある区間にだけ適用されます。ごみ箱のボタン（Delete キー）はその区間を削除し、もう一度押すと元に戻します。その右のボタンは前の区間と結合します。書き出しでは、残した区間を順につなげて 1 つのファイルにします。区間によって画面サイズが異なる場合は、最初の区間のサイズに合わせ、ほかの区間は縦横比を保ったまま黒い余白を付けて収めます。
8. ファイル名、出力形式（MP4 / MKV / MOV）、保存先を決めて「書き出し」を押します。書き出し後のサイズはボタンの下に表示されます。既定は CPU による再エンコードです。書き出し中はキャンセルできます。完了すると映像の上に通知が出て、「フォルダーを開く」で書き出したファイルの場所を開けます。書き出し中に別の動画を開くかウィンドウを閉じると、書き出しを中止するか確認します。

「高速切り出し（画質維持）」にチェックを入れると、映像と音声を再エンコードせずにコピーします。キーフレームの位置で切れるため、指定した秒と前後することがあります。クロップ、回転、ズーム、速度変更、複数区間の書き出しとは併用できません。

キーボード操作: Space で再生／一時停止、← → で設定した秒数だけ移動、`,` `.` で 1 フレーム移動、F で全画面表示の切り替え、Esc で全画面表示またはクロップの終了、Ctrl+O で動画を開く、Ctrl+S でフレーム画像を保存します。編集中は I で開始点、O で終了点を再生位置に設定し、S で分割、Delete で区間の削除と復元を切り替えます。シークバーや音量バーにフォーカスがあるときの矢印キーは、そのバーの操作になります。

表示言語は日本語と英語に対応しています。既定では Windows の表示言語に従い、「設定…」の「表示言語」で切り替えられます。切り替えは次回の起動時に反映されます。

エラーの詳細は `%LOCALAPPDATA%\FrameDock\error.log` に記録されます。

## 制限

- 入力と出力はローカルファイルを対象にし、出力形式は MP4 / MKV / MOV です。
- 再エンコードはソフトウェア `libx264`（H.264）と AAC を使います。GPU エンコーダーは必要ありません。
- HDR 動画の再エンコードは、色調を誤って変換しないよう現在は拒否します。HDR は「高速切り出し（画質維持）」であればメタデータを保ったまま出力できます。
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
