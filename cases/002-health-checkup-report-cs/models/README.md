# models フォルダ

## 写真の読み取り（OCR）のモデル：`models\ocr\`

採血結果の報告書の写真を読むための OCR モデル（PaddleOCR の ONNX 版、Apache-2.0）。**配布 zip に最初から入っている**（約 31MB）。
PC の中だけで動き、外部には送らない。2 つのファイルが無ければ、プログラムは写真の読み取りだけ無効にして動く。

- `PP-OCRv6_det_small.onnx`（文字のある場所を見つける）
- `PP-OCRv6_rec_small.onnx`（その場所の文字を読む。文字の一覧はファイルの中に入っている）

手で入れる場合：PyPI の `rapidocr` 3.10.0 の wheel（zip）を展開し、`rapidocr/models/` の上の 2 ファイルを複製する。
