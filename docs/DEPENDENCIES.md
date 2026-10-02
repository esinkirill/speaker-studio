# Зависимости, сборка и условия распространения

Проверено по локальному Speaker Studio 2.0 и официальным источникам 2026-10-02.
В GitHub опубликованы исходники и документация. Полный частный portable ZIP
с моделью и системными runtime DLL не является публичным release asset.

## Почему полный ZIP не опубликован

Локальный комплект проверен технически: приложение, CPU inference и перенос
работают на проверенном Windows host. Это отдельная проверка от разрешения
распространять каждую зависимость.

Для публичного binary release остаются конкретные вопросы:

- Для app-local MSVC DLL подтверждены подписи Microsoft, но не право автора
  публично распространять эти копии по Visual Studio Distributable Code terms.
  Microsoft указывает, что такое право имеют лицензированные пользователи
  Visual Studio. Разрешение личного использования runtime этого не заменяет.
  [Правила Microsoft](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist?view=msvc-170),
  [app-local deployment](https://learn.microsoft.com/en-us/cpp/windows/redistributing-visual-cpp-files?view=msvc-170).
- В комплекте находятся скомпилированный Essentia WASM и FFmpeg DLL. Notices и
  license texts сохранены; полный corresponding source именно этих сборок,
  включая необходимые scripts и связанные компоненты, не подготовлен как
  публичная доставка. Это нужно завершить перед binary distribution.
  [AGPL, §6](https://raw.githubusercontent.com/MTG/essentia.js/v0.1.3/LICENSE),
  [FFmpeg compliance](https://ffmpeg.org/legal.html).
- MuScriptor weights имеют CC BY-NC 4.0 и дополнительные условия доступа/использования.
  Они не становятся MIT вместе с приложением. Читатель получает их у владельца
  модели и принимает актуальные условия своей учётной записью.
  [Официальная модель](https://huggingface.co/MuScriptor/muscriptor-small).

Вывод аудита: текущий private ZIP **не подготовлен для публичного распространения**.
Это не утверждение, что все перечисленные компоненты запрещено распространять:
публичный комплект возможен после выполнения условий каждого правообладателя.

## Области лицензий

| Компонент | Лицензия / условия | Что опубликовано и как получить dependency |
| --- | --- | --- |
| Собственный C# GUI, timing/playback, MIDI parser; Python transcription adapter; runtime builder | MIT, если в файле не указано другое | Исходники в `speaker-player/`; root MIT не перелицензирует vendors |
| `converter/analyze-rhythm.cjs` | AGPL-3.0-or-later, SPDX в файле | Исходник adapter сохраняет отдельную лицензию |
| InpOut 1.5.0.1, Phil Gibbons / Logix4U | MIT | DLL/driver получить у Highrez, сохранить copyright/license; опубликованные исходники обращения к DLL не включают driver |
| MuScriptor source, Kyutai × Mirelo | MIT | Upstream revision `7f213afecf23bd6a1b8672aa223690ee9807cefb`; upstream copyright сохраняется |
| MuScriptor Small weights/config | CC BY-NC 4.0 + условия model gate | Отдельная загрузка из официального Hugging Face repository |
| Essentia.js 0.1.3 / Essentia WASM | AGPLv3; WASM header допускает later version | Отдельное получение upstream; для публикации binaries обеспечить corresponding source и notices |
| FFmpeg `N-108041-gcc867f2c09-20220906` | Проверенный binary сообщает LGPLv3-or-later, `--enable-version3`; лицензии optional libraries отдельно | Получить FFmpeg самостоятельно; при распространении обеспечить source/условия собственной сборки |
| Node.js | MIT + third-party notices | Официальный Windows x64 runtime, его полный LICENSE сохраняется |
| Python | PSF и bundled third-party notices | Полная Windows x64 CPython distribution для подготовки runtime |
| PyTorch 2.8.0+cpu | BSD-3-Clause + bundled dependencies | Официальный CPU wheel; preserve wheel licenses |
| NumPy и другие Python packages | BSD/MIT/Apache/MPL/PSF и собственные bundled notices | Версии перечислены ниже; `.dist-info` и notices сохраняются |
| Microsoft VC14 DLL | Собственная Microsoft license; отдельные redistribution rights | Для личной сборки использовать законно полученный runtime; для публичного binary release проверить Visual Studio rights |

InpOut 1.5.0.1 переведён владельцем на MIT; это подтверждено
[страницей автора](https://www.highrez.co.uk/Downloads/InpOut32/).
Код MuScriptor и weights разделены лицензиями в
[upstream README](https://github.com/muscriptor/muscriptor#license).
Лицензия [CC BY-NC 4.0](https://creativecommons.org/licenses/by-nc/4.0/)
допускает некоммерческое sharing при соблюдении attribution и остальных условий;
она не является общим разрешением коммерческой эксплуатации.

AGPL не следует описывать как «MIT» или самостоятельно добавлять ей запрет
коммерческого использования. Для закрытого продукта upstream предлагает отдельные
[условия Essentia](https://essentia.upf.edu/licensing_information.html).
Этот документ не делает общего вывода о лицензии любой будущей интеграции:
изменённый/совмещённый продукт нужно оценивать по его фактическому устройству.

## Минимальная сборка приложения

Нужны Windows 10/11 x64, Git и установленный .NET Framework с Framework64
`csc.exe`. Сборка использует WinForms, System.Drawing, System.Core и
System.Web.Extensions. NuGet packages для GUI не нужны.

```powershell
git clone https://github.com/esinkirill/speaker-studio.git
cd speaker-studio\speaker-player
powershell -NoProfile -ExecutionPolicy RemoteSigned -File .\build.ps1
.\SpeakerStudio.exe
```

Открытие GUI не запускает playback. Для проверки без оборудования выбрать
«Диаграмма» / Visual output перед «Играть». MIDI → CSV, библиотека, timing,
график и Visual playback работают без Python, модели и InpOut.

Это сборка основного приложения. Дополнительные dependencies нужны только
для соответствующих функций:

| Функция | Ожидаемые локальные файлы относительно `speaker-player/` |
| --- | --- |
| Physical speaker | `inpoutx64.dll` / `inpout32.dll` из официального InpOut 1.5.0.1 |
| BPM detection из audio | `runtime/node.exe`, `runtime/essentia/essentia-wasm.umd.js`, `runtime/essentia/essentia.js-core.umd.js`, `assets/ffmpeg/ffmpeg.exe` и DLL его сборки |
| MP3 → MIDI | `runtime/transcription/python.exe`, Python packages, `models/muscriptor-small/model.safetensors`, `config.json`, FFmpeg |

Physical output через InpOut использует kernel driver, встроенный ресурсом в DLL.
При первой загрузке driver регистрируется/устанавливается; нужны administrator
rights. «Portable приложение» не означает отсутствие driver installation.
Подключение speaker к правильному разъёму и поддержка аппаратного пути остаются
зависимыми от платы. [Как работает InpOut](https://www.highrez.co.uk/Downloads/InpOut32/).

Для BPM dependency `essentia.js@0.1.3` можно получить через npm или upstream release.
Нужны два файла из `dist/`, перечисленные в таблице, и notices этой версии.
FFmpeg выбирать с поддержкой используемых audio formats; shared build требует
DLL рядом с executable. Node и FFmpeg application adapters обращаются к локальным
путям, а не ищут произвольные executables в PATH.

## Получение модели

1. Открыть [MuScriptor Small](https://huggingface.co/MuScriptor/muscriptor-small)
   в браузере, войти своей учётной записью Hugging Face, прочитать и принять
   условия. Gate включает требования иметь права на обрабатываемую музыку.
2. После получения доступа скачать `model.safetensors` и `config.json`.
   Достаточно браузера; программное получение тоже возможно штатным HF CLI.
3. Хранить их вне Git. Для готового runtime нужный target directory —
   `speaker-player/models/muscriptor-small/`.

Если используется HF CLI, авторизоваться локально по
[официальной инструкции](https://huggingface.co/docs/huggingface_hub/en/guides/cli).
Не публиковать token, не передавать его в issue/чат и не записывать в repository.
Доступ к gated model предоставляется отдельному пользователю;
[описание gate](https://huggingface.co/docs/hub/en/models-gated).

Проверенный Small checkpoint:

```text
model.safetensors bytes: 411888600
SHA256: bbd482c786b895cf7d8f44185073d951adae2ebb8a66f82ca84cd1f84569549c
config: dim=768, num_heads=12, num_layers=14, card=1393
```

Builder отклонит другой checkpoint. Обновление upstream weights требует отдельного
изменения/проверки этого профиля. Application converter использует local weights;
при inference авторизация или отправка audio в сервис не выполняются.

## Опциональная подготовка portable transcription runtime

Это developer workflow на своём компьютере. Он не выполняется автоматически
при clone/build GUI. Runtime builder **копирует уже подготовленные inputs**;
он не устанавливает packages, не скачивает weights и не получает лицензионные права.

Нужно подготовить:

- Полный CPython 3.12 Windows x64 root с `python.exe`, `python3.dll`, `python312.dll`,
  `Lib/`, `DLLs/`, `LICENSE.txt`. `.venv` не заменяет этот base root.
- Отдельный build environment с выбранными Python packages в `Lib/site-packages/`.
- MuScriptor source checkout указанного revision; builder копирует его `muscriptor/`.
- Доступные законным образом Small weights/config.
- Официальный CPU wheel
  `torch-2.8.0+cpu-cp312-cp312-win_amd64.whl` из
  [PyTorch CPU index](https://download.pytorch.org/whl/cpu/torch/).
- x64 `msvcp140.dll`, `vcruntime140.dll`, `vcruntime140_1.dll` из законно
  полученного Microsoft runtime. Для redistribution Microsoft описывает
  Visual Studio Redist directory и лицензионные условия
  [здесь](https://learn.microsoft.com/en-us/cpp/windows/choosing-a-deployment-method?view=msvc-170).

Профиль, фактически проверенный в частном комплекте:
CPython 3.12.14, torch 2.8.0+cpu, NumPy 1.26.4, Microsoft VC14 14.51.36231.0.
Другие inputs/версии не считаются проверенными только из-за совместимого имени.
Полный upstream MuScriptor CLI/web server требует дополнительные dependencies
и NumPy ≥2; здесь проверен **direct model API** с NumPy 1.26.4, FFmpeg decode
и собственным MIDI exporter. [Upstream requirements](https://raw.githubusercontent.com/muscriptor/muscriptor/7f213afecf23bd6a1b8672aa223690ee9807cefb/pyproject.toml).

| Python distribution | Проверенная версия |
| --- | --- |
| certifi | 2026.7.22 |
| charset-normalizer | 3.5.2 |
| einops | 0.8.2 |
| filelock | 3.32.3 |
| fsspec | 2026.7.0 |
| huggingface_hub | 0.36.2 |
| idna | 3.20 |
| Jinja2 | 3.1.6 |
| MarkupSafe | 3.0.3 |
| mido | 1.3.3 |
| mpmath | 1.3.0 |
| networkx | 3.6.1 |
| numpy | 1.26.4 |
| packaging | 26.3 |
| PyYAML | 6.0.3 |
| requests | 2.34.2 |
| safetensors | 0.8.0 |
| sympy | 1.14.0 |
| torch | 2.8.0+cpu |
| tqdm | 4.70.1 |
| typing_extensions | 4.16.0 |
| urllib3 | 2.8.0 |

Для подготовки своего environment можно использовать следующий пример
**из корня repository**. Это команды для читателя; чистая загрузка/установка
этого профиля в рамках аудита не запускалась. Пакеты и weight access получать
только самостоятельно, без чужих authentication caches.

```powershell
py -3.12 -m venv D:\inputs\build-env
& 'D:\inputs\build-env\Scripts\python.exe' -m pip install -r .\docs\transcription-build-requirements.txt
& 'D:\inputs\build-env\Scripts\python.exe' -m pip install 'torch==2.8.0+cpu' --index-url https://download.pytorch.org/whl/cpu
& 'D:\inputs\build-env\Scripts\python.exe' -m pip download --no-deps 'torch==2.8.0+cpu' --index-url https://download.pytorch.org/whl/cpu --dest D:\inputs
git clone https://github.com/muscriptor/muscriptor.git D:\inputs\muscriptor
git -C D:\inputs\muscriptor checkout 7f213afecf23bd6a1b8672aa223690ee9807cefb
```

Весовая модель, полный base Python, FFmpeg и MSVC этим примером не скачиваются.
Название CPU wheel и platform должны совпадать с профилем builder.

Пример вызова из `speaker-player/`: заменить placeholder paths на свои
подготовленные каталоги. Destination должен быть новым: существующие
`runtime/transcription/` и `models/muscriptor-small/` builder не перезаписывает.

```powershell
py -3.12 .\scripts\build-transcription-runtime.py `
  --python-root 'D:\inputs\python312' `
  --site-packages 'D:\inputs\build-env\Lib\site-packages' `
  --muscriptor-source 'D:\inputs\muscriptor' `
  --model-source 'D:\inputs\muscriptor-small' `
  --torch-wheel 'D:\inputs\torch-2.8.0+cpu-cp312-cp312-win_amd64.whl' `
  --vc-runtime 'D:\inputs\vc14-x64' `
  --destination 'D:\SpeakerStudio-local'
```

Builder создаёт только `runtime/transcription/` и `models/muscriptor-small/`.
Для GUI комплектовать destination собственными `src` build output, icon,
`converter/` scripts и отдельно подготовленным FFmpeg; Node/Essentia — по желанию
для BPM. При отсутствии/ошибке BPM analysis transcription пишет nominal 120
как MIDI clock и отмечает `bpmSource=unknown`, а не выдаёт его за измерение.

В текущем reference builder поле `pythonVersion` manifest фиксировано как
`3.12.14`: перед применением к другой CPython distribution нужно исправить
metadata и повторить проверки. Manifest сам по себе не проверяет права
распространения файлов. Исходный CPU wheel занимает около 619 MB и включает
несколько GB build artifacts; builder исключает headers/static libraries/tests.
Проверенный runtime + Small model занимают около 859 MB до ZIP.

После подготовки проверить загрузку без inference:

```powershell
& 'D:\SpeakerStudio-local\runtime\transcription\python.exe' -I -B `
  'D:\SpeakerStudio-local\converter\transcribe-midi.py' --check-runtime
```

Для проверки portability отдельно запускать из другого directory, с ложными
`PYTHONHOME/PYTHONPATH`, проверять фактически загруженные VC14 DLL paths,
валидность MIDI, cancellation и отсутствие mutations исходного audio.
Сборка GUI, model load и физическое звучание — разные проверки.

## Что не включать в Git и публичные artifacts

Не публиковать model weights, private portable ZIP, Microsoft DLL,
локальные virtualenv/runtime folders, authentication caches/tokens,
`data/library.json`, пользовательские MP3/MIDI/CSV, output и experiments.
Для примеров использовать собственные короткие synthetic sequences.
Перед будущим binary release отдельно завершить Microsoft redistribution grant,
model conditions, Essentia corresponding source, FFmpeg exact source/build
instructions и notices всех фактически включённых libraries.
