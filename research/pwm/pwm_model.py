"""Synthetic PWM experiment: no ports, driver, audio device, or real-time loop.

Run with a Python containing NumPy and ReportLab. Graphs are exported as SVG/PDF;
PNG rendering uses the optional --pdftoppm executable. All times are mathematical.
"""

from __future__ import annotations

import argparse
import csv
import json
import math
import platform
import subprocess
import sys
from pathlib import Path

import numpy as np
import reportlab
from reportlab.graphics import renderPDF, renderSVG
from reportlab.graphics.charts.lineplots import LinePlot
from reportlab.graphics.shapes import Drawing, Line, Rect, String
from reportlab.lib.colors import HexColor, black, white


OUT = Path(__file__).resolve().parent
PIT_HZ = 1_193_182.0
CARRIER_HZ = 20_000.0
TONE_HZ = 500.0
CUTOFF_HZ = 2_000.0
DURATION_S = 0.1  # 50 complete tone cycles, 2000 carrier frames.
MODULATION = 0.45
SEED = 29
INK = HexColor("#213045")
GRID = HexColor("#e1e6ed")
COLORS = [HexColor(c) for c in ("#176ba0", "#de7527", "#29916b", "#9865b2")]


def pulse_coefficients(
    centers_s: np.ndarray, widths_s: np.ndarray, duration_s: float, cutoff_hz: float
) -> tuple[np.ndarray, np.ndarray]:
    """Exact Fourier integral of non-overlapping centered, unipolar pulses.

    c_k = sum(w/T * sinc(f_k*w) * exp(-j*2*pi*f_k*center)).
    The finite signal repeats periodically. An ideal zero-phase brick-wall LPF
    keeps only |f| <= cutoff. The formula needs no waveform sampling timestep.
    """
    count = int(math.floor(cutoff_hz * duration_s + 1e-10))
    frequency_hz = np.arange(count + 1, dtype=float) / duration_s
    coefficients = np.empty(count + 1, dtype=complex)
    for k, frequency in enumerate(frequency_hz):
        coefficients[k] = np.sum(
            widths_s / duration_s
            * np.sinc(frequency * widths_s)
            * np.exp(-2j * np.pi * frequency * centers_s)
        )
    return frequency_hz, coefficients


def target_coefficients(
    frequencies_hz: np.ndarray, tones: list[tuple[float, float]]
) -> np.ndarray:
    coefficients = np.zeros(len(frequencies_hz), dtype=complex)
    coefficients[0] = 0.5
    for frequency, amplitude in tones:
        idx = int(np.argmin(np.abs(frequencies_hz - frequency)))
        assert abs(frequencies_hz[idx] - frequency) < 1e-9
        coefficients[idx] = -0.5j * amplitude
    return coefficients


def reconstruct(
    time_s: np.ndarray, frequencies_hz: np.ndarray, coefficients: np.ndarray,
    remove_dc: bool = True,
) -> np.ndarray:
    value = np.zeros(len(time_s)) if remove_dc else np.full(len(time_s), coefficients[0].real)
    for frequency, coefficient in zip(frequencies_hz[1:], coefficients[1:]):
        value += 2 * np.real(coefficient * np.exp(2j * np.pi * frequency * time_s))
    return value


def metrics(
    frequencies_hz: np.ndarray, coefficients: np.ndarray,
    desired: np.ndarray, tones: list[tuple[float, float]],
) -> dict[str, object]:
    """Parseval RMS and NRMSE, after removing each signal's own DC.

    Single-tone THD projects only harmonics 2..floor(cutoff/tone). It excludes
    non-harmonic noise; NRMSE includes that noise in the retained passband.
    THD is deliberately omitted for a mixture with two intended frequencies.
    """
    desired_rms = math.sqrt(2 * float(np.sum(np.abs(desired[1:]) ** 2)))
    error = coefficients[1:] - desired[1:]
    rms = math.sqrt(2 * float(np.sum(np.abs(coefficients[1:]) ** 2)))
    result: dict[str, object] = {
        "dc_mean": float(coefficients[0].real),
        "ac_rms": rms,
        "desired_ac_rms": desired_rms,
        "nrmse_ac": math.sqrt(2 * float(np.sum(np.abs(error) ** 2))) / desired_rms,
        "peak_amplitudes": {},
    }
    tone_indices = []
    for frequency, _ in tones:
        idx = int(np.argmin(np.abs(frequencies_hz - frequency)))
        tone_indices.append(idx)
        result["peak_amplitudes"][str(int(frequency))] = float(2 * abs(coefficients[idx]))
    if len(tones) == 1:
        fundamental_idx = tone_indices[0]
        harmonic_indices = [
            multiple * fundamental_idx
            for multiple in range(2, int(CUTOFF_HZ / tones[0][0]) + 1)
        ]
        harmonic_power = float(np.sum(np.abs(coefficients[harmonic_indices]) ** 2))
        result["thd_harmonics_2_to_4"] = math.sqrt(harmonic_power) / abs(coefficients[fundamental_idx])
        residual = coefficients.copy()
        residual[0] = 0
        residual[[fundamental_idx] + harmonic_indices] = 0
        result["nonharmonic_ac_rms_in_passband"] = math.sqrt(
            2 * float(np.sum(np.abs(residual) ** 2))
        )
    return result


def caption(drawing: Drawing, text: str, x: float, y: float, size: float = 10,
            color=INK, bold: bool = False, anchor: str = "start") -> None:
    drawing.add(String(x, y, text, fontName="Helvetica-Bold" if bold else "Helvetica",
                       fontSize=size, fillColor=color, textAnchor=anchor))


def header(drawing: Drawing, title: str, subtitle: str) -> None:
    drawing.add(Rect(0, 0, drawing.width, drawing.height, fillColor=white, strokeColor=None))
    caption(drawing, title, 34, drawing.height - 32, 19, bold=True)
    caption(drawing, subtitle, 34, drawing.height - 53, 10)


def plot(
    drawing: Drawing, x: float, y: float, width: float, height: float,
    series: list[tuple[np.ndarray, np.ndarray]], limits: tuple[float, float, float, float],
    x_ticks: list[float], y_ticks: list[float], xlabel: str, ylabel: str, title: str,
    colors: list | None = None,
) -> None:
    lp = LinePlot()
    lp.x, lp.y, lp.width, lp.height = x, y, width, height
    lp.data = [list(zip(map(float, sx), map(float, sy))) for sx, sy in series]
    lp.xValueAxis.valueMin, lp.xValueAxis.valueMax = limits[:2]
    lp.yValueAxis.valueMin, lp.yValueAxis.valueMax = limits[2:]
    lp.xValueAxis.valueSteps = x_ticks
    lp.yValueAxis.valueSteps = y_ticks
    for axis in (lp.xValueAxis, lp.yValueAxis):
        axis.strokeColor = INK
        axis.strokeWidth = 0.65
        axis.labels.fontName = "Helvetica"
        axis.labels.fontSize = 9
        axis.visibleGrid = True
        axis.gridStrokeColor = GRID
        axis.gridStrokeWidth = 0.5
    lp.xValueAxis.labels.dy = -5
    lp.yValueAxis.labels.dx = -4
    lp.yValueAxis.labels.boxAnchor = "e"
    for i, color in enumerate(colors or COLORS):
        if i < len(series):
            lp.lines[i].strokeColor = color
            lp.lines[i].strokeWidth = 1.4
    drawing.add(lp)
    caption(drawing, title, x, y + height + 14, 11, bold=True)
    caption(drawing, xlabel, x + width / 2, y - 31, 9, anchor="middle")
    caption(drawing, ylabel, x, y + height + 29, 9)


def legend(drawing: Drawing, items: list[str], x: float, y: float,
           colors: list | None = None, step: float = 160) -> None:
    for idx, (item, color) in enumerate(zip(items, colors or COLORS)):
        drawing.add(Line(x + step * idx, y, x + step * idx + 18, y,
                         strokeColor=color, strokeWidth=2))
        caption(drawing, item, x + step * idx + 24, y - 3, 9)


def save_figure(drawing: Drawing, stem: str, pdftoppm: str | None) -> None:
    figures = OUT / "figures"
    figures.mkdir(parents=True, exist_ok=True)
    renderSVG.drawToFile(drawing, str(figures / f"{stem}.svg"))
    pdf_path = figures / f"{stem}.pdf"
    renderPDF.drawToFile(drawing, str(pdf_path))
    if pdftoppm:
        subprocess.run(
            [pdftoppm, "-png", "-singlefile", "-r", "144", str(pdf_path),
             str(figures / stem)], check=True, capture_output=True, text=True
        )


def duty_figure(pdftoppm: str | None) -> None:
    drawing = Drawing(880, 610)
    header(drawing, "Duty changes the mean and the spectrum",
           "Ideal unipolar pulse: output levels 0 and 1. Same period in every example.")
    for i, duty in enumerate((0.25, 0.5, 0.75)):
        xs, ys = [], []
        for n in range(3):
            xs.extend([n, n, n + duty, n + duty, n + 1])
            ys.extend([0, 1, 1, 0, 0])
        plot(drawing, 72 + 276 * i, 373, 218, 125,
             [(np.array(xs), np.array(ys)), (np.array([0, 3]), np.array([duty, duty]))],
             (0, 3, 0, 1.08), [0, 1, 2, 3], [0, 0.5, 1], "Time / period", "Output level",
             f"D = {duty:.0%}; mean = {duty:.2f}", [COLORS[0], COLORS[1]])
    duty = np.linspace(0, 1, 401)
    plot(drawing, 72, 102, 716, 173,
         [(duty * 100, duty), (duty * 100, np.sin(np.pi * duty))],
         (0, 100, 0, 1.05), [0, 25, 50, 75, 100], [0, 0.25, 0.5, 0.75, 1],
         "Duty (%)", "Relative value", "Mean is monotonic; fundamental peaks at 50%")
    legend(drawing, ["Mean: D", "Fundamental / its 50% value: sin(pi D)"], 104, 59, step=204)
    caption(drawing, "25% and 75% have the same fundamental amplitude, but different DC mean.", 72, 36, 10)
    caption(drawing, "Neither quantity alone is measured acoustic loudness.", 72, 20, 9)
    save_figure(drawing, "01-duty-and-fundamental", pdftoppm)


def reconstruction_figure(
    frequencies: np.ndarray, ideal: np.ndarray,
    centers: np.ndarray, widths: np.ndarray,
    mix_frequencies: np.ndarray, mixture: np.ndarray,
    pdftoppm: str | None,
) -> None:
    drawing = Drawing(880, 748)
    header(drawing, "One binary wire can carry a waveform with multiple tones",
           "Synthetic centered PWM: 20 kHz carrier, one duty update per carrier period, ideal LPF at 2 kHz.")
    t = np.linspace(0, 0.006, 1501)
    target = MODULATION * np.sin(2 * np.pi * TONE_HZ * t)
    plot(drawing, 72, 494, 716, 141,
         [(t * 1000, target), (t * 1000, reconstruct(t, frequencies, ideal))],
         (0, 6, -0.52, 0.52), list(range(7)), [-0.45, 0, 0.45],
         "Time (ms)", "DC removed", "500 Hz target and ideal filtered PWM")
    legend(drawing, ["Target: 0.45 sin(2 pi 500 t)", "Filtered PWM"], 100, 453, step=284)
    pulse_x, pulse_y = [0.0], [0.0]
    for center, width in zip(centers, widths):
        if center > 0.001:
            break
        pulse_x.extend([(center - width / 2) * 1000, (center - width / 2) * 1000,
                        (center + width / 2) * 1000, (center + width / 2) * 1000])
        pulse_y.extend([0, 1, 1, 0])
    pulse_x.append(1.0)
    pulse_y.append(0.0)
    td = np.linspace(0, 0.001, 501)
    plot(drawing, 72, 302, 716, 101,
         [(np.array(pulse_x), np.array(pulse_y)),
          (td * 1000, 0.5 + MODULATION * np.sin(2 * np.pi * TONE_HZ * td))],
         (0, 1, 0, 1.07), [0, 0.25, 0.5, 0.75, 1], [0, 0.5, 1],
         "Time (ms)", "Output / duty", "Pulse width follows samples of D(t) = 0.5 + audio(t)")
    tm = np.linspace(0, 0.01, 1501)
    wanted = 0.2 * np.sin(2 * np.pi * 440 * tm) + 0.2 * np.sin(2 * np.pi * 660 * tm)
    plot(drawing, 72, 93, 716, 118,
         [(tm * 1000, wanted), (tm * 1000, reconstruct(tm, mix_frequencies, mixture))],
         (0, 10, -0.46, 0.46), [0, 2, 4, 6, 8, 10], [-0.4, 0, 0.4],
         "Time (ms)", "DC removed", "Target mixture: 440 Hz + 660 Hz (0.20 peak each)")
    caption(drawing, "The filtered mathematical waveform contains both tones; this is not a speaker measurement.", 72, 34, 10)
    caption(drawing, "Ideal LPF is non-causal and flat below 2 kHz. No coil, amplifier, cone, resonance, or room is modeled.", 72, 17, 9)
    save_figure(drawing, "02-pwm-reconstruction-and-mixture", pdftoppm)


def quantization_figure(
    frequencies: np.ndarray, quantized: np.ndarray,
    jitter_cases: dict[float, np.ndarray], jitter_metrics: list[dict[str, object]],
    pdftoppm: str | None,
) -> None:
    drawing = Drawing(880, 659)
    header(drawing, "Clock quantization and synthetic pulse-width jitter",
           "Not a Windows timing measurement. Jitter is independent Gaussian width error; frame centers stay exact.")
    ds = np.linspace(0.40, 0.60, 801)
    rates = [8000, 16000, 32000, 44100]
    series = [(ds * 100, np.round(ds * PIT_HZ / rate) / (PIT_HZ / rate) * 100) for rate in rates]
    plot(drawing, 72, 341, 324, 176, series,
         (40, 60, 38, 62), [40, 45, 50, 55, 60], [40, 45, 50, 55, 60],
         "Requested duty (%)", "Rounded duty (%)", "Less pulse-width resolution at higher rates")
    legend(drawing, ["8 kHz", "16 kHz", "32 kHz", "44.1 kHz"], 66, 299, step=91)
    x = np.array([float(row["width_error_requested_rms_us"]) for row in jitter_metrics])
    y = np.array([float(row["nrmse_ac"]) * 100 for row in jitter_metrics])
    jitter_y_max = max(6, 2 * math.ceil(max(y) / 2))
    plot(drawing, 500, 341, 288, 176, [(x, y)],
         (0, 5, 0, jitter_y_max), [0, 1, 2, 3, 4, 5],
         list(range(0, jitter_y_max + 1, 2)), "Requested RMS width error (us)", "In-band AC NRMSE (%)",
         "Same 20 kHz / 500 Hz signal; fixed seed")
    caption(drawing, "0 us includes PIT width rounding. Error measured after ideal LPF.", 474, 296, 8)
    ts = np.linspace(0, 0.012, 1601)
    wanted = MODULATION * np.sin(2 * np.pi * TONE_HZ * ts)
    residuals = [reconstruct(ts, frequencies, coeffs) - wanted
                 for coeffs in (quantized, jitter_cases[1.0], jitter_cases[5.0])]
    span = max(0.04, math.ceil(max(float(np.max(abs(v))) for v in residuals) * 100) / 100)
    plot(drawing, 72, 109, 716, 115,
         [(ts * 1000, values) for values in residuals],
         (0, 12, -span, span), [0, 2, 4, 6, 8, 10, 12], [-span, 0, span],
         "Time (ms)", "Filtered output - target", "Width error introduces retained-band error and noise")
    legend(drawing, ["PIT rounding only", "+ 1 us width jitter", "+ 5 us width jitter"], 100, 67, step=220)
    caption(drawing, "At 20 kHz, one carrier period is 50 us; a 1 us width error is 2 percentage points of duty.", 72, 34, 10)
    caption(drawing, "This model omits late/missed updates and correlated jitter; it cannot predict real acoustic quality.", 72, 17, 9)
    save_figure(drawing, "03-quantization-and-synthetic-jitter", pdftoppm)


def run(pdftoppm: str | None) -> dict[str, object]:
    checks: dict[str, bool] = {}
    pulse_rows = []
    for duty in (0.25, 0.5, 0.75):
        frequencies, coefficients = pulse_coefficients(
            np.array([duty / 2]), np.array([duty]), 1.0, 1.0
        )
        amplitude = 2 * abs(coefficients[1])
        analytical = 2 / np.pi * np.sin(np.pi * duty)
        assert abs(coefficients[0].real - duty) < 1e-13
        assert abs(amplitude - analytical) < 1e-13
        pulse_rows.append({"duty": duty, "mean": float(coefficients[0].real),
                           "fundamental_peak": float(amplitude),
                           "fundamental_peak_analytic": float(analytical)})
    checks["pulse_mean_monotonic_25_50_75"] = all(
        pulse_rows[i]["mean"] < pulse_rows[i + 1]["mean"] for i in range(2)
    )
    checks["pulse_fundamental_matches_analytic"] = True
    checks["duty_25_75_equal_fundamental"] = abs(
        pulse_rows[0]["fundamental_peak"] - pulse_rows[2]["fundamental_peak"]
    ) < 1e-13
    frames = int(round(DURATION_S * CARRIER_HZ))
    period = 1 / CARRIER_HZ
    centers = (np.arange(frames) + 0.5) * period
    duty = 0.5 + MODULATION * np.sin(2 * np.pi * TONE_HZ * centers)
    widths = duty * period
    frequencies, ideal = pulse_coefficients(centers, widths, DURATION_S, CUTOFF_HZ)
    wanted = target_coefficients(frequencies, [(TONE_HZ, MODULATION)])
    ideal_metrics = metrics(frequencies, ideal, wanted, [(TONE_HZ, MODULATION)])
    # A finite carrier has measurable baseband distortion even with an ideal LPF.
    # Independent second-order sinc expansion predicts the fundamental amplitude.
    approximate_fundamental = MODULATION * (
        1 - (np.pi * TONE_HZ / CARRIER_HZ) ** 2 / 6
        * (3 * 0.5 ** 2 + 0.75 * MODULATION ** 2)
    )
    checks["ideal_pwm_fundamental_matches_second_order_sinc_expansion"] = abs(
        ideal_metrics["peak_amplitudes"]["500"] - approximate_fundamental
    ) < 2e-6
    checks["ideal_pwm_strongest_ac_line_is_500hz"] = frequencies[1 + np.argmax(abs(ideal[1:]))] == TONE_HZ
    ticks = np.round(widths * PIT_HZ).astype(int)
    quantized_widths = ticks / PIT_HZ
    _, quantized = pulse_coefficients(centers, quantized_widths, DURATION_S, CUTOFF_HZ)
    quantized_metrics = metrics(frequencies, quantized, wanted, [(TONE_HZ, MODULATION)])
    noise = np.random.default_rng(SEED).standard_normal(frames)
    jitter_coeffs = {}
    jitter_metrics = []
    for requested_rms_us in (0.0, 0.1, 1.0, 5.0):
        before_clip = quantized_widths + noise * requested_rms_us * 1e-6
        actual_widths = np.clip(before_clip, 0, period)
        _, coeffs = pulse_coefficients(centers, actual_widths, DURATION_S, CUTOFF_HZ)
        jitter_coeffs[requested_rms_us] = coeffs
        row = metrics(frequencies, coeffs, wanted, [(TONE_HZ, MODULATION)])
        row.update({"width_error_requested_rms_us": requested_rms_us,
                    "width_error_realized_rms_us": float(np.sqrt(np.mean((actual_widths - quantized_widths) ** 2)) * 1e6),
                    "clipped_pulses": int(np.count_nonzero(before_clip != actual_widths))})
        jitter_metrics.append(row)
    checks["chosen_jitter_cases_nrmse_increases"] = all(
        jitter_metrics[i]["nrmse_ac"] < jitter_metrics[i + 1]["nrmse_ac"] for i in range(3)
    )
    mix_tones = [(440.0, 0.2), (660.0, 0.2)]
    mix_duty = 0.5 + sum(amplitude * np.sin(2 * np.pi * frequency * centers)
                         for frequency, amplitude in mix_tones)
    mix_frequencies, mixture = pulse_coefficients(centers, mix_duty * period, DURATION_S, CUTOFF_HZ)
    mix_wanted = target_coefficients(mix_frequencies, mix_tones)
    mix_metrics = metrics(mix_frequencies, mixture, mix_wanted, mix_tones)
    checks["mixture_duty_within_0_and_1"] = 0 < float(min(mix_duty)) < float(max(mix_duty)) < 1
    strongest_mix_lines = set(mix_frequencies[1 + np.argsort(abs(mixture[1:]))[-2:]])
    checks["mixture_two_strongest_ac_lines_are_440_660hz"] = strongest_mix_lines == {440.0, 660.0}
    rate_rows = []
    for rate in (8000, 16000, 32000, 44100):
        period_ticks = PIT_HZ / rate
        rate_rows.append({"rate_hz": rate, "frame_us": 1e6 / rate,
                          "nominal_frame_pit_ticks": period_ticks,
                          "frame_length_if_pit_clocked_ticks": [math.floor(period_ticks), math.ceil(period_ticks)],
                          "nonzero_counts_fitting_short_frame_ignoring_io_overhead": [1, math.floor(period_ticks)],
                          "nominal_duty_quantum_percentage_points": 100 / period_ticks,
                          "maximum_rounding_error_percentage_points": 50 / period_ticks,
                          "nyquist_hz": rate / 2})
    checks = {name: bool(value) for name, value in checks.items()}
    checks["all_numerical_checks_passed"] = all(checks.values())
    assert checks["all_numerical_checks_passed"], checks
    data = {
        "model_only": True,
        "hardware_io": False,
        "machine_timing_measured": False,
        "runtime": {"python": sys.version, "numpy": np.__version__,
                    "reportlab": reportlab.Version, "platform": platform.platform()},
        "hypotheses": [
            "Unipolar pulse mean equals duty, but same-frequency fundamental follows sin(pi*D).",
            "A PWM duty stream can represent a bounded sum of two tones after an ideal low-pass filter.",
            "Pulse-width clock quantization and chosen synthetic width errors add retained-band error.",
        ],
        "parameters": {"pit_clock_hz_assumed": PIT_HZ, "carrier_hz": CARRIER_HZ,
                       "carrier_period_us": period * 1e6, "tone_hz": TONE_HZ,
                       "modulation_amplitude": MODULATION, "dc_bias": 0.5,
                       "ideal_lowpass_cutoff_hz": CUTOFF_HZ, "duration_s": DURATION_S,
                       "carrier_frames": frames, "fourier_bin_hz": 1 / DURATION_S,
                       "jitter_seed": SEED, "alignment": "center-aligned pulse in each exact carrier frame"},
        "definitions": {
            "lowpass": "Exact pulse Fourier integral, then discard frequencies above 2 kHz. Periodic extension, zero-phase non-causal ideal filter, no physical transfer function.",
            "ac_rms": "sqrt(2*sum(abs(c_k)^2)), k>0 in retained passband, each signal DC removed.",
            "nrmse_ac": "RMS(filtered PWM AC - target AC)/RMS(target AC); no amplitude/phase fitting. Includes in-band harmonic and non-harmonic error.",
            "thd": "sqrt(A_1000^2 + A_1500^2 + A_2000^2)/A_500, coherent Fourier projections only; non-harmonic noise excluded. Not THD+N, not full-band THD.",
            "jitter": "Same seeded independent Gaussian errors added to quantized pulse widths, RMS requested in microseconds; widths clipped to [0,50 us], frame centers exact. Synthetic, not measured Windows scheduling jitter.",
            "pit_quantization": "Round each mathematical pulse width to nearest assumed PIT tick. Exact 20 kHz frame grid is retained as a modeling assumption, not a claim of hardware attainability.",
            "pit_rate_table": "Tick budget in nominal frame. Counts 1..floor(ticks) ignore all I/O/setup overhead and describe resolution, not a ready PIT programming sequence. PIT count register value 0 means 65536, not zero-length pulse; zero/full duty need explicit boundary handling.",
        },
        "pulse_duty_experiment": pulse_rows,
        "single_tone_ideal_pwm": ideal_metrics,
        "single_tone_second_order_sinc_peak_approximation": float(approximate_fundamental),
        "single_tone_pit_width_quantized_pwm": quantized_metrics,
        "pulse_width_counts_at_20khz": {"minimum": int(min(ticks)), "maximum": int(max(ticks)),
                                        "distinct_counts_for_this_500hz_wave": int(len(set(ticks))),
                                        "tick_us": 1e6 / PIT_HZ},
        "synthetic_width_jitter_scenarios": jitter_metrics,
        "two_tone_ideal_pwm": {**mix_metrics, "duty_min": float(min(mix_duty)),
                               "duty_max": float(max(mix_duty)), "desired_tones_hz_peak": mix_tones},
        "pit_rate_resolution": rate_rows,
        "checks": checks,
        "limitations": [
            "No timing, driver, motherboard speaker, voltage, acoustic pressure, or loudness is measured.",
            "Ideal LPF is an illustrative reconstruction assumption; real speaker response and carrier attenuation are unknown.",
            "Width-error scenarios omit correlated jitter, edge asymmetry, late/missed frames, finite I/O duration, and OS stalls.",
            "Resolution table does not claim those rates can be sustained by user-space InpOut or a PIT mode.",
            "Fixed periodic finite-window noise and retained-band THD are defined analysis choices, not predicted hardware specifications.",
        ],
        "rejected_initial_hypothesis": {
            "hypothesis": "An ideal LPF makes this finite-carrier PWM reconstruct the target with less than 0.1% NRMSE.",
            "result": "Rejected: single-tone NRMSE 0.1731%, two-tone NRMSE 0.1814%. Finite pulse widths introduce baseband harmonic/intermodulation error even before quantization and jitter.",
        },
    }
    (OUT / "results").mkdir(parents=True, exist_ok=True)
    (OUT / "results" / "pwm-model-results.json").write_text(
        json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    with (OUT / "results" / "synthetic-jitter-metrics.csv").open("w", newline="", encoding="utf-8") as stream:
        columns = ["width_error_requested_rms_us", "width_error_realized_rms_us", "clipped_pulses",
                   "dc_mean", "ac_rms", "nrmse_ac", "thd_harmonics_2_to_4", "nonharmonic_ac_rms_in_passband"]
        writer = csv.DictWriter(stream, fieldnames=columns, extrasaction="ignore")
        writer.writeheader()
        writer.writerows(jitter_metrics)
    duty_figure(pdftoppm)
    reconstruction_figure(frequencies, ideal, centers, widths, mix_frequencies, mixture, pdftoppm)
    quantization_figure(frequencies, quantized, jitter_coeffs, jitter_metrics, pdftoppm)
    return data


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pdftoppm", help="Optional existing Poppler pdftoppm.exe path for PNG outputs")
    args = parser.parse_args()
    data = run(args.pdftoppm)
    print(json.dumps({"checks": data["checks"], "ideal": data["single_tone_ideal_pwm"],
                      "quantized": data["single_tone_pit_width_quantized_pwm"],
                      "mixture": data["two_tone_ideal_pwm"],
                      "output_directory": str(OUT)}, indent=2))


if __name__ == "__main__":
    main()
