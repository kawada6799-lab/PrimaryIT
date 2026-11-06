# models フォルダ

## 写真の読み取り（OCR）のモデル：`models\ocr\`

採血結果の報告書の写真を読むための OCR モデル（PaddleOCR の ONNX 版、Apache-2.0）。**配布 zip に最初から入っている**（約 31MB）。
PC の中だけで動き、外部には送らない。2 つのファイルが無ければ、プログラムは写真の読み取りだけ無効にして動く。

- `PP-OCRv6_det_small.onnx`（文字のある場所を見つける）
- `PP-OCRv6_rec_small.onnx`（その場所の文字を読む。文字の一覧はファイルの中に入っている）

手で入れる場合：PyPI の `rapidocr` 3.10.0 の wheel（zip）を展開し、`rapidocr/models/` の上の 2 ファイルを複製する。

## 音声入力のモデル：`models\ggml-small.bin`（任意）

音声入力（オフラインの音声認識 Whisper）のモデルファイルの置き場所。無くてもプログラムは動く（音声入力だけ無効になる）。

- モデルは GitHub Actions の成果物 **`kenshin-002-voice-model`**（zip、約 470MB）に入っている。展開してできる `models\ggml-small.bin` をこのフォルダに置く。
- 一度置けば更新不要。プログラムを入れ替えるときも `models\` は残す。
- 手でダウンロードする場合：`https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin`（アカウント不要）。
- 精度を上げたいときは `ggml-medium.bin`（約 1.5GB）を置き、`config\config.json` の `voice.modelPath` を変える。
