# MuScriptor Small: установка и интеграция

Speaker Studio использует [MuScriptor Small](https://huggingface.co/MuScriptor/muscriptor-small) от Kyutai × Mirelo для локального MP3/WAV → MIDI. Модель возвращает ноты и инструментальные партии; плеер затем выбирает партию и сводит её к одному голосу для speaker.

В [релизе 2.0.2](https://github.com/esinkirill/speaker-studio/releases/tag/v2.0.2) уже находятся веса, исходники модели и окружение Python для CPU. Для конвертации не нужны GPU или учётная запись Hugging Face.

## Установка готового приложения

Требуется Windows 10 22H2 или Windows 11, x64.

1. Распакуй `SpeakerStudio-2.0.2-win-x64-with-model.zip` целиком.
2. Если подходящего VC runtime нет, установи [Microsoft Visual C++ v14 x64](https://aka.ms/vc14/vc_redist.x64.exe).
3. Запусти `Prepare-Audio.cmd` с интернетом. Он скачает фиксированную сборку FFmpeg от BtbN и Essentia.js, проверит контрольные суммы архивов и разместит компоненты в папке приложения.
4. Открой `SpeakerStudio.exe` → MP3 → MIDI, выбери аудиофайл и нажми «Создать MIDI». Результаты сохраняются в `output/` внутри папки приложения.

Python, PyTorch и Node отдельно устанавливать не нужно. После подготовки конвертация работает без интернета.

## Расположение файлов

```text
SpeakerStudio.exe
Prepare-Audio.cmd
Prepare-Audio.ps1
converter/
  transcribe-midi.py
  analyze-rhythm.cjs
models/muscriptor-small/
  config.json
  model.safetensors
runtime/
  node.exe
  essentia/                       # появляется после Prepare-Audio
  transcription/
    python.exe
    python312._pth
    Lib/
    DLLs/
    site-packages/                # torch, numpy, muscriptor, mido и др.
    runtime-manifest.json
assets/ffmpeg/                    # появляется после Prepare-Audio
licenses/
```

`python312._pth` задаёт локальные каталоги импорта. C# передаёт путь к конвертеру, а Python находит веса в `models/muscriptor-small/` относительно корня приложения. Файлы модели и окружения должны находиться рядом с EXE в этой структуре.

## Модель и версия исходников

MuScriptor Small содержит около 103 млн параметров. Исходник зафиксирован на коммите `7f213afecf23bd6a1b8672aa223690ee9807cefb`:

```text
model.safetensors: 411888600 bytes
SHA256: bbd482c786b895cf7d8f44185073d951adae2ebb8a66f82ca84cd1f84569549c
config: dim=768, num_heads=12, num_layers=14, card=1393
```

Веса в комплекте не изменены. Исходники модели имеют лицензию MIT, веса — CC BY-NC 4.0; сведения об авторах находятся в `licenses/`.

## Как выполняется конвертация

[MidiConversion.cs](../speaker-player/src/MidiConversion.cs) запускает Node для оценки BPM, затем локальный Python для транскрипции. [transcribe-midi.py](../speaker-player/converter/transcribe-midi.py) декодирует аудио через FFmpeg в одноканальный float32 16 кГц и вызывает `TranscriptionModel.transcribe` для фрагментов по 5 секунд.

| Параметр | Значение |
| --- | --- |
| Вычисления | CPU |
| Декодирование | Жадное, `use_sampling=False` |
| CFG | `cfg_coef=1` |
| Prelude | `prelude_forcing=True` |
| Размер пакета | `batch_size=1` |

Экспорт сохраняет инструментальные партии и одновременные высоты. Velocity фиксирована на 64. Ноты обрезаются по длительности аудио; наложения одной высоты в одной партии сокращаются до следующей атаки. Конвертер не добавляет квантование или выравнивание до такта.

Результаты рядом с выбранным MIDI:

- `.mid` — ноты и инструментальные партии;
- `.transcription.json` — параметры модели, длительность и события;
- `.rhythm.json` — оценка BPM и сетки, если анализ ритма завершился успешно.

Если BPM определить не удалось, 120 используется как опорный темп для записи MIDI ticks. Метаданные помечают BPM неизвестным; исходные времена нот сохраняются. После выбора партии плеер отдельно строит одноголосную последовательность. Различия распознавания и выбора одного голоса описаны в [исследовании обработки мелодии](RESEARCH.md).

## Подключение к сборке из Git

Готовое окружение Python можно взять из релиза:

1. Собери `speaker-player/build.ps1`.
2. Скопируй `runtime/`, `models/`, `licenses/`, `inpout32.dll` и `inpoutx64.dll` из распакованного релиза в `speaker-player/`.
3. Выполни `speaker-player/Prepare-Audio.cmd`. Если аудиокомпоненты уже подготовлены, скопируй также `assets/ffmpeg/`.
4. Запусти собранный EXE.

Для загрузки модели без конвертации аудио:

```powershell
cd speaker-player
.\runtime\transcription\python.exe -I -B .\converter\transcribe-midi.py --check-runtime
```

Команда проверяет импорт модулей, загружает локальные веса на CPU и выводит JSON с путями модулей, версиями и временем загрузки. Аудио при этом не распознаётся.

## Сборка собственного окружения Python для CPU

Нужен полный CPython 3.12 x64 с `Lib/` и `DLLs/`. Виртуальное окружение используется для подготовки пакетов; `--python-root` должен указывать на базовую установку Python. Версии приложения: Python 3.12.14, Torch 2.8.0+cpu и NumPy 1.26.4.

Из корня клонированного репозитория, пример рабочей папки `D:\inputs`:

```powershell
py -3.12 -m venv D:\inputs\build-env
& 'D:\inputs\build-env\Scripts\python.exe' -m pip install -r .\docs\transcription-build-requirements.txt
& 'D:\inputs\build-env\Scripts\python.exe' -m pip install 'torch==2.8.0+cpu' --index-url https://download.pytorch.org/whl/cpu
& 'D:\inputs\build-env\Scripts\python.exe' -m pip download --no-deps 'torch==2.8.0+cpu' --index-url https://download.pytorch.org/whl/cpu --dest D:\inputs
git clone https://github.com/muscriptor/muscriptor.git D:\inputs\muscriptor
git -C D:\inputs\muscriptor checkout 7f213afecf23bd6a1b8672aa223690ee9807cefb
```

`model.safetensors` и `config.json` доступны в релизе и на [официальной странице Small](https://huggingface.co/MuScriptor/muscriptor-small/tree/main). Для скачивания с официальной страницы требуется вход и доступ к модели. Размести файлы в `D:\inputs\muscriptor-small`.

[Скрипт сборки окружения](../speaker-player/scripts/build-transcription-runtime.py) копирует CPython, пакеты и исходники модели, распаковывает wheel PyTorch для CPU и создаёт `runtime-manifest.json`. Заголовочные файлы, статические `.lib` и тесты Torch исключаются.

```powershell
py -3.12 .\speaker-player\scripts\build-transcription-runtime.py `
  --python-root D:\inputs\Python312 `
  --site-packages D:\inputs\build-env\Lib\site-packages `
  --muscriptor-source D:\inputs\muscriptor `
  --model-source D:\inputs\muscriptor-small `
  --torch-wheel 'D:\inputs\torch-2.8.0+cpu-cp312-cp312-win_amd64.whl' `
  --destination D:\inputs\SpeakerRuntime
```

Замени `--python-root` на каталог полного Python. Каталог назначения должен быть новым: скрипт не перезаписывает существующие каталоги окружения и модели. Полученные `runtime/transcription/` и `models/` скопируй к приложению вместе с лицензионными уведомлениями. VC++ v14 x64 устанавливается официальным установщиком; опциональный `--vc-runtime` задаёт каталог DLL для размещения рядом с приложением.

Скрипт использует подготовленные файлы и не скачивает веса или пакеты. Это окружение обслуживает прямой API модели; у веб-сервера MuScriptor свои зависимости.

## Если конвертация не запускается

| Симптом | Действие |
| --- | --- |
| «MP3 → MIDI не подготовлен» | Запустить Prepare-Audio; проверить наличие `ffmpeg.exe`, `python.exe`, `config.json` и весов |
| Missing VCRUNTIME140 / MSVCP140 | Установить официальный VC++ v14 **x64**, затем повторить подготовку |
| «Checkpoint differs» при сборке runtime | Использовать Small с SHA256 выше |
| MIDI содержит бас и аккорды | Выбрать ведущую партию перед CSV; полный MIDI сохраняет многоголосие |
| Медленная конвертация | Начать с короткого фрагмента: транскрипция выполняется на CPU |

Программные тесты описаны в [руководстве по тестам](../speaker-player/tests/README.md); устройство интеграции — в [описании архитектуры](ARCHITECTURE.md).
