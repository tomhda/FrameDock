# SILframe のビルド

[English](BUILDING.md)

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

Release は `artifacts\release\win-x64` に生成されます。スクリプトは Release 中間生成物を clean してから発行し、WinUI の `App.xbf`、`MainWindow.xbf`、`SILframe.pri` を含むことを確認します。`SILframe.exe` と同じ場所に `libmpv-2.dll`、`Media\ffmpeg.exe`、`Media\ffprobe.exe` が配置され、`ThirdPartyNotices` にライセンスとビルド情報が入ります。

依存物は `vendor\`、ダウンロードしたアーカイブと一時ファイルは `tools\.cache\` に置かれます。これらはブートストラップが管理する生成物です。別の配置先で動作確認する場合は、`-InstallRoot` と `-CacheRoot` を指定できます。ローカルに保管したアーカイブを使う場合は `-MpvArchivePath` と `-FfmpegArchivePath` を渡せますが、固定 SHA-256 に合わないファイルは拒否されます。

```powershell
$bootstrapScript = Join-Path (Join-Path $PWD 'tools') 'bootstrap-dependencies.ps1'
& $bootstrapScript -InstallRoot 'D:/SILframe-stage' -CacheRoot 'D:/SILframe-cache' -MpvArchivePath 'D:/archives/mpv-dev-lgpl-x86_64-20260925-git-35af06172b.7z' -FfmpegArchivePath 'D:/archives/ffmpeg-9.0.2-essentials_build.7z'
```

`build-release.ps1` は既定で依存を再検証します。すでに取得済みでネット接続がない場合は `-SkipDependencyBootstrap` を指定できます。その場合も `vendor\` 内の全バイナリとライセンス文書の SHA-256 を照合します。出力先を変えるときは、リポジトリ内のパスを `-OutputDirectory` に指定してください。

## Windows インストーラー

Inno Setup はビルド時だけ使用し、インストール先には同梱しません。商用利用時は [Inno Setup のライセンス案内](https://jrsoftware.org/isorder.php) を確認してください。

Windows x64 で次を実行すると、Release を作成して一つのオフライン対応セットアップ EXE をビルドします。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\build-installer.ps1
```

出力先は `artifacts\installer\SILframe-Setup-<バージョン>-win-x64.exe` です（バージョンは `Directory.Build.props` の `Version` で決まり、アプリ、`silframe-cli`、セットアップで共通です）。初回のビルドは固定バージョンの Inno Setup 7.1.0 を公式 GitHub リリースから取得し、SHA-256 と Authenticode 署名を確認して `tools\.cache\` 内だけに展開します。セットアップには Release のアプリ、WinUI リソース、libmpv、FFmpeg、ライセンス通知が入るため、インストール先で追加ダウンロードはありません。既存 Release を使う場合は `-SkipReleaseBuild` を指定できます。

セットアップはユーザーごとに `%LOCALAPPDATA%\Programs\SILframe` へインストールし、スタートメニューの起動項目とアンインストーラーを作成します。デスクトップアイコンと一般的な動画形式の「プログラムから開く」登録は任意です。Windows の既定アプリは変更しません。セットアップはコード署名をしていません。

## リリースに添付するソース一式

FFmpeg は GPLv3、libmpv は LGPL で配布されているため、リリースには同梱バイナリのソースを一緒に公開します。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\build-source-bundle.ps1
```

同梱バイナリと同じコミットの FFmpeg、x264、mpv のソースアーカイブを取得し、SHA-256 を確認して `artifacts\sources` に書き出します。`SOURCES.md`、FFmpeg ビルドの `README.txt`、`SHA256SUMS.txt` も同じ場所に入ります。このフォルダーのファイルをすべて、セットアップ EXE と一緒にリリースへ添付します。内容の説明は [SOURCES.md](SOURCES.md) にあります。

リリースを公開したら、`README.md` と `README.ja.md` にあるダウンロードリンク（冒頭と「インストール」の節）のバージョンを新しいものに書き換えます。

固定している FFmpeg または mpv のビルドを変更したときは、`tools\build-source-bundle.ps1` と `docs\SOURCES.md` のコミットとハッシュも更新します。

## テスト

Core ライブラリのビルドと、FFmpeg を使わない幾何チェック:

```powershell
dotnet build .\SILframe.Core\SILframe.Core.csproj
dotnet run --project .\SILframe.Core.Tests\SILframe.Core.Tests.csproj
```

同梱 FFmpeg でエンコード・ffprobe 統合チェックも実行するには、次の環境変数を設定してからテストハーネスを起動します。

```powershell
$env:SILFRAME_TEST_FFMPEG = (Resolve-Path .\vendor\ffmpeg\ffmpeg.exe).Path
$env:SILFRAME_TEST_FFPROBE = (Resolve-Path .\vendor\ffmpeg\ffprobe.exe).Path
dotnet run --project .\SILframe.Core.Tests\SILframe.Core.Tests.csproj
```

両方の環境変数が設定されていない場合、外部ツールを使う統合チェックは明示的に `SKIP` と表示されます。テスト項目と Core の座標・時間契約は [EXPORT-NOTES.md](../EXPORT-NOTES.md) に記載しています。

## 画面の文言

画面の文言は `SILframe\Strings\<言語>\Resources.resw`、Core ライブラリのメッセージは `SILframe.Core\Resources\Messages.resx`（英語）と `Messages.<言語>.resx` にあります。文言を変更または追加したあとは、すべての言語でキーとプレースホルダーの数が一致していることを確認します。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\check-strings.ps1
```

## 検証用の環境変数

| 変数 | 効果 |
|---|---|
| `SILFRAME_LANGUAGE` | `ja-JP` または `en-US`。表示言語を上書きします。 |
| `SILFRAME_SETTINGS_PATH` | `%LOCALAPPDATA%\SILframe\settings.json` の代わりに使う設定ファイルのパス。 |
| `SILFRAME_OPEN_EDITOR` | `1` にすると、動画を読み込んだ直後に編集パネルを開きます。 |
| `SILFRAME_MPV_LOG` | `1` にすると、mpv のログを `%TEMP%\SILframe-mpv.log` に書き出します。 |
| `SILFRAME_SEEK_PREVIEW_AT` | `0`〜`1` の値。ポインターを使わずに、シークバーのその位置のサムネイルを表示し続けます。 |
