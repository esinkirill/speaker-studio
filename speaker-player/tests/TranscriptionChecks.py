"""Pure transcription export checks: no model inference or audio output."""

from __future__ import annotations

import importlib.util
import io
from pathlib import Path
import unittest

import mido

script = Path(__file__).resolve().parent.parent / "converter" / "transcribe-midi.py"
spec = importlib.util.spec_from_file_location("speaker_transcription", script)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


def note(pitch=60, start=0.1, end=1.0, program=0, instrument="piano", drum=False):
    return {"pitch": pitch, "start": start, "end": end, "program": program,
            "instrument": instrument, "is_drum": drum}


class ExportChecks(unittest.TestCase):
    def test_polyphonic_tracks_and_timing(self):
        notes = [note(60, 0.1, 1.0), note(64, 0.1, 1.0), note(67, 0.1, 1.0),
                 note(40, 0.3, 1.2, 32, "bass"), note(38, 0.25, 0.25, 128, "drums", True)]
        normalized, discarded = module.normalize_notes(notes, 20.123)
        self.assertEqual(discarded, 0)
        data, count, channel_reuse = module.make_midi(normalized, 20.123, 146.038, "estimated")
        midi = mido.MidiFile(file=io.BytesIO(data))
        self.assertEqual(midi.type, 1)
        self.assertEqual(count, 3)
        self.assertFalse(channel_reuse)
        self.assertEqual(len(midi.tracks), 4)
        self.assertEqual(sum(m.type == "note_on" and m.velocity > 0 for t in midi.tracks for m in t), 5)
        self.assertEqual({m.note for t in midi.tracks for m in t if m.type == "note_on"}, {38, 40, 60, 64, 67})
        self.assertTrue(any(m.type == "program_change" and m.program == 32 for t in midi.tracks for m in t))
        self.assertTrue(any(m.type == "note_on" and m.channel == 9 for t in midi.tracks for m in t))
        self.assertTrue(any(m.type == "marker" and m.text == "muscriptor:bar_offset=0" for t in midi.tracks for m in t))
        self.assertTrue(any(m.type == "marker" and m.text == "speakerstudio:bpm_source=estimated" for t in midi.tracks for m in t))
        self.assertLess(abs(midi.length - 20.123), 0.001)
        played = [m for m in midi if m.type == "note_on"]
        self.assertEqual(played[0].note, 60)
        elapsed = 0
        for message in midi:
            elapsed += message.time
            if message.type == "note_on":
                break
        self.assertLess(abs(elapsed - 0.1), 0.001)
        self.assertEqual(notes[-1]["end"], 0.25, "Normalization must not mutate source events")

    def test_clip_padding_without_moving_notes(self):
        source = [note(60, -0.3, 0.4), note(62, 0.5, 100), note(64, 30, 31)]
        result, discarded = module.normalize_notes(source, 20)
        self.assertEqual(discarded, 1)
        self.assertEqual([(n["start"], n["end"]) for n in result], [(0, 0.4), (0.5, 20)])
        self.assertEqual(source[0]["start"], -0.3)

    def test_same_pitch_overlap_preserves_attacks(self):
        result, discarded = module.normalize_notes([note(60, 0, 1), note(60, 0.8, 2), note(64, 0.2, 1.5)], 3)
        self.assertEqual(discarded, 0)
        same_pitch = [n for n in result if n["pitch"] == 60]
        self.assertEqual([(n["start"], n["end"]) for n in same_pitch], [(0, 0.8), (0.8, 2)])
        self.assertEqual([n["end"] for n in result if n["pitch"] == 64], [1.5])

    def test_nonfinite_or_empty_events_discarded(self):
        result, discarded = module.normalize_notes([note(start=float("nan")), note(end=float("inf")),
            note(end=0.1), note(pitch=128)], 20)
        self.assertEqual(result, [])
        self.assertEqual(discarded, 4)

    def test_empty_transcription_keeps_audio_duration(self):
        data, count, reused = module.make_midi([], 12.345, 120)
        midi = mido.MidiFile(file=io.BytesIO(data))
        self.assertEqual(count, 0)
        self.assertFalse(reused)
        self.assertEqual(len(midi.tracks), 1)
        self.assertLess(abs(midi.length - 12.345), 0.001)
        self.assertTrue(any(m.type == "marker" and m.text == "speakerstudio:bpm_source=unknown" for t in midi.tracks for m in t))

    def test_midi_channel_limit_is_reported(self):
        notes = [note(program=p, instrument=f"program_{p}") for p in range(16)]
        data, count, reused = module.make_midi(notes, 2, 120)
        midi = mido.MidiFile(file=io.BytesIO(data))
        self.assertEqual(count, 16)
        self.assertTrue(reused)
        self.assertEqual(len(midi.tracks), 17)
        self.assertTrue(all(m.channel != 9 for t in midi.tracks for m in t if m.type == "note_on"))


if __name__ == "__main__":
    unittest.main()
