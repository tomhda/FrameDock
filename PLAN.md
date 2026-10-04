# SILframe 実装方針

## 構成

SILframe は .NET 8 と WinUI 3 で動く Windows x64 の動画プレイヤーです。ローカル動画を開き、再生、フレーム移動、音量・速度調整、フレーム画像の保存を行い、同じウィンドウで範囲指定、クロップ、回転、ズーム、速度変更、分割をして書き出します。

- `SILframe`: 画面（WinUI 3、非パッケージ）。再生は libmpv、画面の文言は `Strings\<言語>\Resources.resw`。
- `SILframe.Core`: メディア情報の取得と書き出し。同梱の `ffprobe` / `ffmpeg` を別プロセスで呼び出す。利用者向けメッセージは `Resources\Messages*.resx`。
- `SILframe.Core.Tests`: テストフレームワークを使わない検査ハーネス。
- `tools`: 依存物の取得と検証、Release の発行、インストーラーとソース一式の作成、文言の検査。

## 方針

映像は libmpv の D3D11 composition 出力を WinUI の `SwapChainPanel` に接続して表示します。操作 UI、クロップ選択、ズーム位置の調整、通知は、WinUI の通常のコントロールとして同じ画面に重ねます。

起動と通常の再生を重くしないことを前提にします。編集用の処理（サムネイルの生成、区間の管理、プレビューの切り替え）は、編集パネルを開いているときだけ動かします。

メディア情報は同梱 `ffprobe` で取得し、書き出しは同梱 `ffmpeg` に任せます。既定は CPU の `libx264` による再エンコードです。編集をしていない単一区間は、再エンコードなしの高速切り出しも選べますが、キーフレーム位置による近似の切り出しとして扱います。

クロップ座標は、回転を適用した正方ピクセルの表示面を基準にします。SAR による表示幅を反映し、90 度・270 度の回転では縦横を入れ替えます。4:2:0 出力用に、位置と大きさを偶数ピクセルにそろえます。ズームは出力サイズを変えず、プレビューと書き出しで同じ計算（`MediaGeometry.ComputeZoomSampleRect`）を使います。

分割した区間は、それぞれクロップ、回転、ズーム、速度を持ちます。書き出しでは、残した区間を順につなげて 1 つのファイルにし、最初の区間のサイズを出力サイズにします。

表示言語は日本語と英語です。既定では Windows の表示言語に従います。言語を追加するときは、`Resources.resw` と `Messages.<言語>.resx` を足し、`tools\check-strings.ps1` でキーとプレースホルダーの一致を確認します。

依存バイナリ、配布元、SHA-256、ライセンス文書を bootstrap で固定し、Release の配置とインストーラーの作成を PowerShell スクリプトで再現します。配布物には FFmpeg の GPLv3 文書、mpv の LGPLv2.1 以降の文書、Windows App SDK のライセンスを含めます。リリースには、同梱バイナリのソース一式（`tools\build-source-bundle.ps1`）を添付します。SILframe 自体のライセンスは GPLv3 です。

## 確認している内容

- Core の検査ハーネス 29 項目（同梱 FFmpeg を使う統合検査を含む）。回転と SAR を含むクロップ、ズーム、H.264 / H.265 の出力、MP4 / MKV / MOV、速度変更、開始時刻が 0 でない入力、複数区間の書き出し、高速切り出し、HDR の扱い、キャンセルと元ファイルの保護、日英のメッセージ。
- `tools\check-strings.ps1` による日英の文言の一致。
- Release の発行とインストーラーの作成、インストールした版の起動と再生。

## 文書

- 使い方と制限: [`README.md`](README.md)（日本語版は [`README.ja.md`](README.ja.md)）
- ビルド、テスト、インストーラー、ソース一式: [`docs/BUILDING.md`](docs/BUILDING.md)（日本語版は [`docs/BUILDING.ja.md`](docs/BUILDING.ja.md)）
- 書き出しライブラリの仕様: [`EXPORT-NOTES.md`](EXPORT-NOTES.md)
- 同梱物とライセンス: [`THIRD-PARTY-LICENSES.md`](THIRD-PARTY-LICENSES.md)、ソースの入手: [`docs/SOURCES.md`](docs/SOURCES.md)
