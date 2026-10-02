# Как я встроил MuScriptor Small

Хороший MIDI оказался гораздо полезнее попытки сразу превратить MP3 в одну
частоту. Поэтому я встроил [MuScriptor Small](https://huggingface.co/MuScriptor/muscriptor-small)
от **Kyutai × Mirelo**: модель возвращает ноты и группы инструментов, после чего
можно выбрать нужную партию для пищалки.

В [релизе 2.0.1](https://github.com/esinkirill/speaker-studio/releases/tag/v2.0.1)
модель уже есть. Ни Hugging Face account, ни загрузка весов при конвертации не
нужны. GPU тоже не нужен: мой профиль использует CPU.

## Установка готового приложения

1. Распаковать `SpeakerStudio-2.0.1-win-x64-with-model.zip` целиком.
2. При отсутствии подходящего VC runtime поставить
   [Microsoft Visual C++ v14 x64](https://aka.ms/vc14/vc_redist.x64.exe).
3. Запустить `Prepare-Audio.cmd` с интернетом: он подготовит FFmpeg и Essentia.
4. Открыть EXE → вкладка MP3 → MIDI → выбрать audio и папку результата.

Не нужно ставить PyTorch через pip на машине, где запускается готовый комплект.
Вся Python часть находится рядом с EXE. Windows минимум — 10 22H2 / 11 x64.

## Файлы, которые реально нужны

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
  essentia/                       # появляется после подготовки
  transcription/
    python.exe
    python312._pth
    Lib/
    DLLs/
    site-packages/                # torch, numpy, muscriptor, mido и др.
    runtime-manifest.json
assets/ffmpeg/                    # появляется после подготовки
licenses/
```

Одних `config.json` и весов недостаточно: нативные Torch/NumPy и точный model
код тоже должны быть доступны локальному Python. `python312._pth` привязывает
imports к комплекту. C# передаёт путь к конвертеру, а Python находит модель
относительно корня приложения в `models/muscriptor-small/`.

Исходник MuScriptor зафиксирован на revision
`7f213afecf23bd6a1b8672aa223690ee9807cefb`. Проверенный Small checkpoint:

```text
model.safetensors: 411888600 bytes
SHA256: bbd482c786b895cf7d8f44185073d951adae2ebb8a66f82ca84cd1f84569549c
config: dim=768, num_heads=12, num_layers=14, card=1393
```

Веса я не менял. Source и attribution лежат в комплекте; модель — CC BY-NC 4.0.

## Контракт между C# и Python

[MidiConversion.cs](../speaker-player/src/MidiConversion.cs) запускает локальный
Python как отдельный процесс. [transcribe-midi.py](../speaker-player/converter/transcribe-midi.py)
декодирует audio через FFmpeg в mono float32 16 kHz, загружает Small и вызывает
`TranscriptionModel.transcribe` кусками по 5 секунд. В текущем профиле:
CPU, greedy decoding, CFG=1, prelude forcing, batch size=1.

Exporter сохраняет все предсказанные партии и одновременные ноты. Velocity
фиксирован на 64: модель его не предсказывает. Концы padded chunks обрезаются
по длительности audio; повторные ноты одной высоты и инструмента сокращаются
до следующей атаки. Автоматический quantization и дополнительный bar padding
не добавляю.

Node adapter независимо оценивает BPM через Essentia. Результат конвертации:

- `.mid` — многоголосные ноты и инструменты;
- `.transcription.json` — параметры, версия, длительность и результаты модели;
- `.rhythm.json` — оценка BPM и устойчивости сетки.

Если BPM определить не удалось, 120 используется только как clock reference
для записи MIDI. Metadata помечает его неизвестным; приложение не выдаёт это
значение за найденный темп композиции.

После выбора партии плеер сводит её к одной линии. Модель и этот mono algorithm
решают разные задачи, поэтому повышение качества модели само по себе не
гарантирует идеальную мелодию на speaker.

## Подключить комплект к сборке из Git

Самый короткий developer workflow — взять готовый runtime из релиза:

1. Собрать `speaker-player/build.ps1`.
2. Скопировать `runtime/`, `models/`, `licenses/` и InpOut DLL из распакованного
   релиза в `speaker-player/`.
3. Выполнить `speaker-player/Prepare-Audio.cmd`. Если setup уже запускался в
   релизе, можно скопировать также `assets/ffmpeg/`.
4. Запустить свой EXE. Он использует те же converters из исходников.

Для диагностики без воспроизведения:

```powershell
cd speaker-player
.\runtime\transcription\python.exe -I -B .\converter\transcribe-midi.py --check-runtime
```

Команда проверяет imports и реально загружает локальную модель на CPU,
но не распознаёт audio. В результате выводится время загрузки.

## Собрать собственный CPU runtime

Этот путь нужен, если хочется самостоятельно подготовить Python часть.
Использую полный **CPython 3.12 x64** с `Lib/` и `DLLs/`; один `.venv` не заменяет
base installation. Проверенный комплект собран с Python 3.12.14,
Torch 2.8.0+cpu и NumPy 1.26.4.

Подготовка из корня clone, пример рабочей папки `D:\inputs`:

```powershell
py -3.12 -m venv D:\inputs\build-env
& 'D:\inputs\build-env\Scripts\python.exe' -m pip install -r .\docs\transcription-build-requirements.txt
& 'D:\inputs\build-env\Scripts\python.exe' -m pip install 'torch==2.8.0+cpu' --index-url https://download.pytorch.org/whl/cpu
& 'D:\inputs\build-env\Scripts\python.exe' -m pip download --no-deps 'torch==2.8.0+cpu' --index-url https://download.pytorch.org/whl/cpu --dest D:\inputs
git clone https://github.com/muscriptor/muscriptor.git D:\inputs\muscriptor
git -C D:\inputs\muscriptor checkout 7f213afecf23bd6a1b8672aa223690ee9807cefb
```

Весовые файлы можно взять из моего релиза или скачать с
[официальной страницы Small](https://huggingface.co/MuScriptor/muscriptor-small/tree/main).
Для скачивания у upstream требуется вход и доступ к модели. Нужны
`model.safetensors` и `config.json`, размести их в `D:\inputs\muscriptor-small`.

[Builder](../speaker-player/scripts/build-transcription-runtime.py) копирует
CPython, выбранные dependencies и model source, извлекает CPU wheel и создаёт
manifest. Он исключает headers, static `.lib` и tests Torch. Команда:

```powershell
py -3.12 .\speaker-player\scripts\build-transcription-runtime.py `
  --python-root D:\inputs\Python312 `
  --site-packages D:\inputs\build-env\Lib\site-packages `
  --muscriptor-source D:\inputs\muscriptor `
  --model-source D:\inputs\muscriptor-small `
  --torch-wheel 'D:\inputs\torch-2.8.0+cpu-cp312-cp312-win_amd64.whl' `
  --destination D:\inputs\SpeakerRuntime
```

`--python-root` замени на каталог своего полного Python. Destination должен
быть новым: builder не перезаписывает существующие runtime/model directories.
Полученные `runtime/transcription/` и `models/` скопируй к приложению вместе с
notices. VC++ x64 runtime устанавливается отдельно официальным installer.

Builder не скачивает weights и не устанавливает packages; environment из
команд выше является его входными данными. Это сборка direct model API профиля,
а не установка отдельного MuScriptor web server.

## Если конвертация не запускается

| Симптом | Что проверить |
| --- | --- |
| «MP3 → MIDI не подготовлен» | Выполнен ли `Prepare-Audio.cmd`; существуют ли `ffmpeg.exe`, `python.exe`, config и weights |
| Missing VCRUNTIME140 / MSVCP140 или import error Torch | Установить официальный VC++ v14 **x64**, затем повторить setup |
| «Checkpoint differs» | Используется ли Small с SHA-256 выше; builder отклоняет другую модель |
| MIDI с басом и аккордами | Выбрать ведущую партию перед CSV; полный MIDI намеренно многоголосный |
| Медленная конвертация | Это CPU-профиль; сначала попробовать короткий фрагмент |

Проверки интеграции и измеренные времена приведены в
[VERIFICATION.md](VERIFICATION.md); исследование качества — в
[RESEARCH.md](RESEARCH.md).

