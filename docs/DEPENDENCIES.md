# Зависимости и источники

Релиз 2.0.2 включает приложение, MuScriptor Small, окружение Python для CPU, Node и InpOut. FFmpeg и Essentia.js загружаются командой `Prepare-Audio.cmd`. Для нативных Python/Torch библиотек нужен системный Microsoft Visual C++ v14 x64.

## Компоненты

| Компонент | Версия | Источник и расположение |
| --- | --- | --- |
| Speaker Studio | 2.0.2, C# 5 / .NET Framework 4 / AnyCPU | Исходники `speaker-player/src/`, `SpeakerStudio.exe` |
| InpOut | 1.5.0.1 | [Highrez / Phil Gibbons](https://www.highrez.co.uk/Downloads/InpOut32/), DLL рядом с EXE |
| MuScriptor Small | Около 103M параметров | [Kyutai × Mirelo](https://huggingface.co/MuScriptor/muscriptor-small), `models/muscriptor-small/` |
| Исходники MuScriptor | `7f213afecf23bd6a1b8672aa223690ee9807cefb` | [Репозиторий авторов](https://github.com/muscriptor/muscriptor), пакет в `runtime/transcription/site-packages/` |
| CPython | 3.12.14, Windows x64 | [Python](https://www.python.org/), `runtime/transcription/` |
| PyTorch | 2.8.0+cpu, cp312 win_amd64 | [Индекс CPU-сборок](https://download.pytorch.org/whl/cpu/torch/), `runtime/transcription/site-packages/` |
| NumPy | 1.26.4 | [NumPy](https://numpy.org/), прямой API модели |
| Node.js | 24.19.0, Windows x64 | [Node.js](https://nodejs.org/), `runtime/node.exe` |
| FFmpeg | 8.1.3-9-g29e619e767, win64 LGPL shared | [Сборка BtbN](https://github.com/BtbN/FFmpeg-Builds/releases/tag/autobuild-2026-09-30-13-08), `assets/ffmpeg/` |
| Essentia.js | 0.1.3, UMD + WASM | [Пакет npm](https://www.npmjs.com/package/essentia.js/v/0.1.3), `runtime/essentia/` |
| Visual C++ runtime | Microsoft v14, x64 | [Официальный установщик](https://aka.ms/vc14/vc_redist.x64.exe), устанавливается в систему |

Для интерфейса используется установленный .NET Framework. Сборка из исходников описана в [руководстве по запуску](../README.md).

## Аудиокомпоненты

[Скрипт подготовки аудиокомпонентов](../speaker-player/Prepare-Audio.ps1) получает архив FFmpeg по фиксированному адресу и SHA256, архив Essentia из npm — по адресу версии 0.1.3 и SHA512. Лицензии и уведомления авторов сохраняются рядом с компонентами, источники записываются в `data/audio-tools.json`. Скрипт не устанавливает пакеты Python и не меняет системный PATH.

Роли компонентов:

- FFmpeg декодирует аудио;
- Node + Essentia оценивают BPM;
- Python + MuScriptor распознают ноты;
- InpOut обеспечивает I/O для физического speaker.

После подготовки аудиокомпоненты используются локально, без интернета.

## Пакеты Python и модель

Версии пакетов Python перечислены в [списке зависимостей для сборки Python](transcription-build-requirements.txt). Torch CPU устанавливается из отдельного индекса. Инструкции по сборке окружения, версии исходников модели, конфигурации и SHA256 весов находятся в [руководстве по модели](MODEL.md).

`runtime-manifest.json` содержит пути, размеры и SHA256 файлов окружения и модели. Приложение обращается к MuScriptor через прямой API с NumPy 1.26.4; для веб-сервера MuScriptor нужны его собственные зависимости.

## Лицензии

Приложение на C# и адаптер на Python — MIT; `analyze-rhythm.cjs` — AGPL-3.0-or-later. InpOut и исходники MuScriptor — MIT. Веса Small — CC BY-NC 4.0.

Лицензии находятся в `licenses/`, файле Python `LICENSE.txt` и каталогах пакетов `.dist-info/`. У скачанных FFmpeg/Essentia уведомления авторов сохраняются рядом с компонентами.

Первичные источники: [MuScriptor](https://github.com/muscriptor/muscriptor#license), [описание и условия модели](https://huggingface.co/MuScriptor/muscriptor-small), [InpOut](https://www.highrez.co.uk/Downloads/InpOut32/), [Essentia](https://github.com/MTG/essentia.js/blob/v0.1.3/LICENSE), [BtbN](https://github.com/BtbN/FFmpeg-Builds).
