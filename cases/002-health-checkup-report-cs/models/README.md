# models フォルダ

音声入力（オフラインの音声認識 Whisper）のモデルファイルの置き場所。無くてもプログラムは動く（音声入力だけ無効になる）。

- モデルは GitHub Actions の成果物 **`kenshin-002-voice-model`**（zip、約 470MB）に入っている。展開してできる `models\ggml-small.bin` をこのフォルダに置く。
- 一度置けば更新不要。プログラムを入れ替えるときも `models\` は残す。
- 手でダウンロードする場合：`https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin`（アカウント不要）。
- 精度を上げたいときは `ggml-medium.bin`（約 1.5GB）を置き、`config\config.json` の `voice.modelPath` を変える。
