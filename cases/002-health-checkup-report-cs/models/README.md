# models フォルダ

音声入力（オフラインの音声認識 Whisper）のモデルファイルを置く場所。無くてもプログラムは動く（音声入力だけ無効になる）。
ダウンロードの手順は 1 つ上の README.md「音声入力のモデルを入れる」を参照。

- `ggml-small.bin`（約 470MB、日本語対応、推奨）
- `ggml-medium.bin`（約 1.5GB、精度を上げたいとき。config.json の voice.modelPath を変える）
