# FrameDock 実装方針と残作業

## 実装方針

FrameDock は .NET 8 と WinUI 3 で動く Windows x64 の動画プレイヤーです。ローカル動画を開き、再生、フレーム移動、音量・速度調整、スクリーンショット保存を行い、同じウィンドウで範囲指定とクロップを書き出します。

映像は libmpv の D3D11 composition 出力を WinUI の `SwapChainPanel` に接続して表示します。操作 UI とクロップ選択オーバーレイは WinUI の通常のコントロールとして同じ画面に重ねます。映像専用の子 HWND に表示する旧案は使いません。

メディア情報は同梱 `ffprobe` で取得し、MP4 の書き出しは同梱 `ffmpeg` に任せます。正確な再エンコードが標準で、H.264 は CPU の `libx264` を使います。クロップなしの高速コピーも選べますが、キーフレーム位置による近似トリムとして扱います。

クロップ座標は右角回転を適用した正方ピクセルの表示面を基準にします。SAR による表示幅を反映し、奇数回転では縦横を入れ替えます。4:2:0 出力用の位置と矩形サイズは偶数ピクセルにそろえます。

依存バイナリ、配布元、SHA-256、ライセンス文書を bootstrap で固定し、Release x64 の配置と起動手順を PowerShell スクリプトで再現します。配布物には FFmpeg の GPLv3 文書、mpv の LGPLv2.1 以降文書、Windows App SDK のライセンスを含めます。

## 確認済み

- Release を clean して再発行し、WinUI の XAML/PRI resource、同梱 DLL、FFmpeg、ライセンス文書を検証した。`tools/run.ps1` からの起動でメインウィンドウが表示されることも確認した。
- Core の FFmpeg/ffprobe 統合ハーネス 18 項目が通り、回転/SAR のクロップ、正確な H.264/H.265 出力、MP4/MKV/MOV、速度変更、開始時刻が 0 でない入力の後半の切り出し、近似ストリームコピー、HDR 方針、キャンセルと元ファイル保護を確認した。

## 残作業と確認

- Release 画面で動画再生、フレーム保存、クロップ操作、正確な書き出し、高速コピーを手動で一通り確認する。
- PNG フレーム生成後のクリップボードコピー修正を含め、画像コピー操作を再確認する。
- ユーザー向けの制限とビルド・テスト手順は [`README.md`](README.md) と [`EXPORT-NOTES.md`](EXPORT-NOTES.md)、依存物の詳細は [`THIRD-PARTY-LICENSES.md`](THIRD-PARTY-LICENSES.md) に記載する。
