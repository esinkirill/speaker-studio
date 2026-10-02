"""Offline MuScriptor-small audio transcription, preserving instrument tracks.

The entry point uses the portable interpreter and local safetensors shipped
with Speaker Studio. It never installs packages, downloads models or plays audio.
"""

from __future__ import annotations

import argparse
import hashlib
import io
import json
import math
import os
from pathlib import Path
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parent.parent
SOURCE_REVISION = "7f213afecf23bd6a1b8672aa223690ee9807cefb"


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def decode_audio(source: Path):
    import numpy as np

    command = [str(ROOT / "assets" / "ffmpeg" / "ffmpeg.exe"),
               "-hide_banner", "-loglevel", "error", "-i", str(source),
               "-vn", "-ac", "1", "-ar", "16000", "-f", "f32le", "pipe:1"]
    result = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                            creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
    if result.returncode:
        raise ValueError("Не удалось прочитать аудио: " + result.stderr.decode("utf-8", "replace").strip())
    if not result.stdout or len(result.stdout) % 4:
        raise ValueError("Аудио не содержит корректных звуковых данных.")
    # A writable contiguous array avoids sharing the immutable bytes buffer.
    samples = np.frombuffer(result.stdout, dtype="<f4").copy()
    if not np.all(np.isfinite(samples)):
        raise ValueError("В аудио обнаружены некорректные значения.")
    return samples, 16000, result.stderr.decode("utf-8", "replace").strip()


def make_midi(notes: list[dict], duration: float, bpm: float, bpm_source: str = "unknown"):
    """Retain every instrument and simultaneous pitch; use absolute tick rounding."""
    import mido

    tempo = round(60_000_000 / bpm)
    ticks_per_beat = 960
    midi = mido.MidiFile(type=1, ticks_per_beat=ticks_per_beat)
    ticks_per_second = 1_000_000 * ticks_per_beat / tempo
    total_ticks = max(1, round(duration * ticks_per_second))
    metadata = mido.MidiTrack()
    metadata.extend([
        mido.MetaMessage("track_name", name="MuScriptor small"),
        mido.MetaMessage("set_tempo", tempo=tempo),
        mido.MetaMessage("marker", text="muscriptor:bar_offset=0"),
        mido.MetaMessage("marker", text="speakerstudio:bpm_source=" + bpm_source),
        mido.MetaMessage("end_of_track", time=total_ticks),
    ])
    midi.tracks.append(metadata)
    instruments = sorted({(n["is_drum"], n["program"], n["instrument"]) for n in notes})
    channels = [channel for channel in range(16) if channel != 9]
    tonal_index = 0
    for is_drum, program, name in instruments:
        channel = 9 if is_drum else channels[tonal_index % len(channels)]
        if not is_drum:
            tonal_index += 1
        track = mido.MidiTrack()
        track.append(mido.MetaMessage("track_name", name=name))
        if not is_drum:
            track.append(mido.Message("program_change", channel=channel, program=program))
        events = []
        for note in notes:
            if (note["is_drum"], note["program"], note["instrument"]) != (is_drum, program, name):
                continue
            begin = max(0, min(total_ticks - 1, round(note["start"] * ticks_per_second)))
            end = min(total_ticks, max(begin + 1, round(note["end"] * ticks_per_second)))
            events.append((begin, 1, note["pitch"]))
            events.append((end, 0, note["pitch"]))
        events.sort()
        previous = 0
        for tick, on, pitch in events:
            track.append(mido.Message("note_on" if on else "note_off", channel=channel,
                                      note=pitch, velocity=64 if on else 0, time=tick - previous))
            previous = tick
        track.append(mido.MetaMessage("end_of_track", time=total_ticks - previous))
        midi.tracks.append(track)
    buffer = io.BytesIO()
    midi.save(file=buffer)
    return buffer.getvalue(), len(instruments), tonal_index > len(channels)


def normalize_notes(raw_notes: list[dict], duration: float) -> tuple[list[dict], int]:
    """Clip model padding and resolve duplicate same-instrument/pitch overlaps."""
    groups: dict[tuple, list[dict]] = {}
    discarded = 0
    for original in raw_notes:
        note = dict(original)
        raw_start = float(note["start"])
        raw_end = float(note["end"])
        if not math.isfinite(raw_start) or not math.isfinite(raw_end):
            discarded += 1
            continue
        start = max(0.0, raw_start)
        end = min(duration, raw_end)
        if note["is_drum"]:
            end = min(duration, max(end, start + 0.04))
        if not math.isfinite(start) or not math.isfinite(end) or end <= start or not 0 <= note["pitch"] <= 127:
            discarded += 1
            continue
        note.update(start=start, end=end)
        groups.setdefault((note["instrument"], note["program"], note["pitch"], note["is_drum"]), []).append(note)
    normalized = []
    for group in groups.values():
        group.sort(key=lambda n: (n["start"], n["end"]))
        for index, note in enumerate(group):
            if index + 1 < len(group):
                note["end"] = min(note["end"], group[index + 1]["start"])
            if note["end"] > note["start"]:
                normalized.append(note)
            else:
                discarded += 1
    normalized.sort(key=lambda n: (n["start"], n["program"], n["pitch"], n["end"]))
    return normalized, discarded


def main(argv: list[str] | None = None) -> dict:
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace", line_buffering=True)
        sys.stderr.reconfigure(encoding="utf-8", errors="replace", line_buffering=True)
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--report", type=Path)
    parser.add_argument("--bpm", type=float, default=120.0)
    parser.add_argument("--bpm-source", choices=["unknown", "estimated", "manual"], default="unknown")
    parser.add_argument("--threads", type=int, default=2)
    parser.add_argument("--check-runtime", action="store_true")
    args = parser.parse_args(argv)
    if not math.isfinite(args.bpm) or not 20 <= args.bpm <= 400:
        parser.error("BPM должен быть в диапазоне 20..400.")
    if not 1 <= args.threads <= 16:
        parser.error("Число CPU threads должно быть в диапазоне 1..16.")
    if not args.check_runtime and (args.input is None or args.output is None):
        parser.error("Укажите --input AUDIO и --output MIDI.")
    if not args.check_runtime:
        source = args.input.resolve()
        output = args.output.resolve()
        report_path = args.report.resolve() if args.report else output.with_suffix(".transcription.json")
        if source == output or source == report_path or output == report_path:
            raise ValueError("Исходный файл и результаты должны иметь разные пути.")
        if not source.is_file():
            raise FileNotFoundError("Аудиофайл не найден.")
        if output.exists() or report_path.exists():
            raise FileExistsError("Результат уже существует. Выберите другое имя файла.")

    # Local paths only; model loading must never consult a user HF account/cache.
    os.environ["HF_HUB_OFFLINE"] = "1"
    os.environ["HF_HUB_DISABLE_IMPLICIT_TOKEN"] = "1"
    os.environ["TRANSFORMERS_OFFLINE"] = "1"
    os.environ["HF_HUB_DISABLE_TELEMETRY"] = "1"
    for key in ("HF_TOKEN", "HUGGING_FACE_HUB_TOKEN"):
        os.environ.pop(key, None)
    began = time.perf_counter()
    weights = ROOT / "models" / "muscriptor-small" / "model.safetensors"
    if not weights.is_file() or not weights.with_name("config.json").is_file():
        raise FileNotFoundError("В комплекте нет модели MuScriptor small и её config.json.")
    decode_seconds = 0.0
    if not args.check_runtime:
        print("STATUS Чтение аудио...", flush=True)
        samples, sample_rate, warnings = decode_audio(source)
        decode_seconds = time.perf_counter() - began
        duration = len(samples) / sample_rate
    print("STATUS Загрузка локальной модели MuScriptor small (CPU)...", flush=True)
    load_began = time.perf_counter()
    import torch
    import numpy as np
    from muscriptor import NoteEndEvent, TranscriptionModel
    from muscriptor.events import ProgressEvent
    torch.set_num_threads(args.threads)
    model = TranscriptionModel.load_model(weights, device="cpu")
    load_seconds = time.perf_counter() - load_began
    if args.check_runtime:
        result = {"format": "speaker-transcription-runtime-check-v1", "passed": True,
                  "torchVersion": torch.__version__, "numpyVersion": np.__version__,
                  "pythonVersion": sys.version.split()[0], "pythonPath": sys.executable,
                  "modulePaths": {"torch": torch.__file__, "numpy": np.__file__},
                  "modelPath": str(weights), "modelLoadSeconds": load_seconds,
                  "device": "cpu", "cudaAvailable": torch.cuda.is_available()}
        print(json.dumps(result, ensure_ascii=False), flush=True)
        return result

    audio = torch.from_numpy(samples).unsqueeze(0)
    inference_start = time.perf_counter()
    raw_notes = []
    with torch.inference_mode():
        for event in model.transcribe((audio, sample_rate), use_sampling=False, cfg_coef=1.0,
                                      prelude_forcing=True, batch_size=1):
            if isinstance(event, ProgressEvent):
                print(f"PROGRESS {event.completed} {event.total}", flush=True)
            elif isinstance(event, NoteEndEvent):
                instrument = event.start_event.instrument
                raw_notes.append({"pitch": int(event.start_event.pitch),
                                  "start": float(event.start_event.start_time), "end": float(event.end_time),
                                  "instrument": instrument,
                                  "program": 128 if instrument == "drums" else model._program_for_instrument(instrument),
                                  "is_drum": instrument == "drums"})
    inference_seconds = time.perf_counter() - inference_start
    notes, discarded = normalize_notes(raw_notes, duration)
    data, instrument_count, channel_reuse = make_midi(notes, duration, args.bpm, args.bpm_source)
    report = {
        "format": "speaker-midi-transcription-v1", "source": str(source), "output": str(output),
        "sourceSha256": sha256(source), "modelSha256": sha256(weights),
        "sourceRevision": SOURCE_REVISION, "model": "MuScriptor small", "device": "cpu",
        "torchVersion": torch.__version__, "numpyVersion": np.__version__,
        "audioDurationSeconds": duration, "bpm": args.bpm, "bpmSource": args.bpm_source,
        "barOffsetSeconds": 0, "modelLoadSeconds": load_seconds, "decodeSeconds": decode_seconds,
        "inferenceSeconds": inference_seconds, "totalSeconds": time.perf_counter() - began,
        "noteCount": len(notes), "rawNoteCount": len(raw_notes), "instrumentCount": instrument_count,
        "drumCount": sum(n["is_drum"] for n in notes), "discardedCount": discarded,
        "midiChannelReuse": channel_reuse, "decoderWarnings": warnings, "notes": notes,
        "limitations": ["Transcription is a model estimate, not verified sheet music.",
                        "Model predicts notes/instruments; exported velocity is fixed at 64.",
                        "Original note timing is retained without beat quantization or bar padding.",
                        "When BPM is unknown, 120 is only the MIDI timing reference.",
                        "More than 15 simultaneous tonal instruments share MIDI channels."],
    }
    output.parent.mkdir(parents=True, exist_ok=True)
    report_path.parent.mkdir(parents=True, exist_ok=True)
    # Exclusive creation protects a second application instance from overwrites.
    with report_path.open("x", encoding="utf-8") as stream:
        json.dump(report, stream, ensure_ascii=False, indent=2)
        stream.write("\n")
    try:
        with output.open("xb") as stream:
            stream.write(data)
    except Exception:
        report_path.unlink(missing_ok=True)
        raise
    print(f"DONE {output}", flush=True)
    print(f"STATUS Готово: {len(notes)} нот, {instrument_count} инструментов; {duration:.2f} с.", flush=True)
    return report


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        print(f"ERROR {type(error).__name__}: {error}", file=sys.stderr, flush=True)
        sys.exit(1)
