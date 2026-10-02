# Компоненты и источники

В релизе 2.0.1 я собрал приложение с моделью и CPU runtime. Два аудиокомпонента
получаются отдельно при первом запуске `Prepare-Audio.cmd`; после подготовки
они находятся в папке приложения и используются без интернета.

## Состав комплекта

| Компонент | Версия / профиль | Откуда взят и где лежит |
| --- | --- | --- |
| Speaker Studio | 2.0.1, C#5 / .NET Framework4 / AnyCPU | Исходники `speaker-player/src/`, собственный EXE |
| InpOut | 1.5.0.1 | [Highrez / Phil Gibbons](https://www.highrez.co.uk/Downloads/InpOut32/), DLL рядом с EXE |
| MuScriptor Small | 103M, unchanged checkpoint | [Kyutai × Mirelo](https://huggingface.co/MuScriptor/muscriptor-small), `models/muscriptor-small/` |
| MuScriptor source | `7f213afecf23bd6a1b8672aa223690ee9807cefb` | [Upstream](https://github.com/muscriptor/muscriptor), Python package в `runtime/transcription/site-packages/` |
| CPython | 3.12.14, Windows x64 | [Python](https://www.python.org/), `runtime/transcription/` |
| PyTorch | 2.8.0+cpu, cp312 win_amd64 | [CPU index](https://download.pytorch.org/whl/cpu/torch/), runtime без development headers/static libraries |
| NumPy | 1.26.4 | Проверенный direct model API профиль |
| Node | Windows x64 | `runtime/node.exe`; полный upstream LICENSE в `licenses/` |
| FFmpeg | 8.1.3-9-g29e619e767, win64 LGPL shared | [Фиксированная сборка BtbN](https://github.com/BtbN/FFmpeg-Builds/releases/tag/autobuild-2026-09-30-13-08), скачивается в `assets/ffmpeg/` |
| Essentia.js | 0.1.3, UMD + WASM | [npm package](https://www.npmjs.com/package/essentia.js/v/0.1.3), скачивается в `runtime/essentia/` |
| Visual C++ runtime | Актуальный Microsoft v14, x64 | [Официальный installer](https://aka.ms/vc14/vc_redist.x64.exe), системный prerequisite |

FFmpeg archive фиксирован по SHA-256, Essentia npm archive — по SHA-512.
Адреса и контрольные суммы находятся в
[Prepare-Audio.ps1](../speaker-player/Prepare-Audio.ps1).
Скрипт сохраняет upstream notices и `data/audio-tools.json` с источниками.
Он не устанавливает Python packages и не меняет системный PATH.

Для GUI используется установленный .NET Framework; сборка описана в
[README](../README.md). InpOut нужен только физическому выходу. Node + Essentia
оценивают BPM, Python + MuScriptor распознают ноты, FFmpeg декодирует audio.

## Воспроизвести Python профиль

Версии небольших Python packages зафиксированы в
[transcription-build-requirements.txt](transcription-build-requirements.txt).
`runtime-manifest.json` в готовом комплекте содержит размеры и SHA-256
runtime/model файлов. Процедура подготовки — в [MODEL.md](MODEL.md).

Я вызываю прямой model API, поэтому не устанавливаю весь web/CLI stack MuScriptor.
Профиль с NumPy 1.26.4 относится к этому пути; его не следует переносить на
upstream web server, у которого свои requirements.

## Attribution

Мой C# и Python adapter — MIT; `analyze-rhythm.cjs` — AGPL-3.0-or-later.
InpOut и MuScriptor source — MIT. Веса Small — CC BY-NC 4.0.
Python, Node, Torch и остальные packages сохраняют собственные license files
в `licenses/`, Python `LICENSE.txt` и package `.dist-info/`.
У скачиваемых FFmpeg/Essentia notices остаются рядом с их файлами.

Источники лицензий: [MuScriptor](https://github.com/muscriptor/muscriptor#license),
[model card](https://huggingface.co/MuScriptor/muscriptor-small),
[InpOut](https://www.highrez.co.uk/Downloads/InpOut32/),
[Essentia](https://github.com/MTG/essentia.js/blob/v0.1.3/LICENSE),
[BtbN](https://github.com/BtbN/FFmpeg-Builds).

