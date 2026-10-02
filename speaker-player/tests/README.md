# Тестирование

Команды ниже выполняются из корня репозитория. Основной набор требует Windows x64, PowerShell и .NET Framework 4.x со штатным `csc.exe`.

## Сборка и основной набор

```powershell
.\speaker-player\build.ps1
.\speaker-player\test.ps1
.\speaker-player\test.ps1 -Platform x86
```

[build.ps1](../build.ps1) создаёт `speaker-player/SpeakerStudio.exe`. [test.ps1](../test.ps1) компилирует и запускает suites для выбранной разрядности; по умолчанию используется x64.
Входные MIDI/CSV создаются самими тестами. Проверки playback используют Visual backend.

| Suite | Что проверяет |
| --- | --- |
| [TimingChecks](TimingChecks.cs) | Транспонирование, скорость, gap, Quantize/Chop, glide и округление границ с сохранением общей длительности. |
| [SequenceDataChecks](SequenceDataChecks.cs) | Чтение CSV/MIDI, tempo metadata, повторные атаки, mono projection и обработку некорректных данных. |
| [PlaybackTests](PlaybackTests.cs) | Timing deadlines, pause/resume, seek, loop, stop/restart и события сессии при параллельных действиях. |
| [ProcessTests](ProcessTests.cs) | Аргументы с пробелами и Unicode, stdout/stderr, отмену запуска и завершение дерева дочерних процессов. |

Результаты выполненных suites записываются в `speaker-player/build/tests/x64/test-results.json` или `speaker-player/build/tests/x86/test-results.json`.
EXE и временные файлы каждого suite находятся рядом, в его подкаталоге. `ProcessTests` также пишет `ProcessTests/process-check.json` с результатом отмены.

## Optional: график и piano roll

Нужна интерактивная Windows desktop session:

```powershell
.\speaker-player\test.ps1 -IncludeVisualUi
```

[TimelineVisualChecks](TimelineVisualChecks.cs) проверяет выбор позиции, границы графика, DPI, подписи нот, паузы и кривую частоты.
PNG сохраняются в `speaker-player/build/tests/x64/TimelineVisualChecks/`; при `-Platform x86` используется соответствующий каталог x86.

## Optional: Python MIDI export

Для [TranscriptionChecks.py](TranscriptionChecks.py) нужны Python 3.10+ и Mido. Зависимость удобно поставить в отдельный venv:

```powershell
python -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r speaker-player/tests/requirements.txt
.\.venv\Scripts\python.exe speaker-player/tests/TranscriptionChecks.py
```

Проверяются полифонические партии, note-on/off, сохранение длительности, обрезка model padding, повторные ноты, некорректные события и MIDI channels.
Это тесты функций `normalize_notes` и `make_midi`; результат печатает `unittest` в консоль.

Сборка runtime и запуск самой транскрипции описаны отдельно в [инструкции по интеграции модели](../../docs/MODEL.md).
