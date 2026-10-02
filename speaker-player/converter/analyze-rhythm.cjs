/* SPDX-License-Identifier: AGPL-3.0-or-later */
'use strict';

const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const { spawnSync } = require('node:child_process');
const { performance } = require('node:perf_hooks');

function options(argv) {
  const result = {};
  for (let i = 0; i < argv.length; i += 2) {
    const key = argv[i];
    if (!['--input', '--output'].includes(key) || argv[i + 1] === undefined || result[key.slice(2)] !== undefined) {
      throw new Error('Use --input AUDIO --output JSON.');
    }
    result[key.slice(2)] = argv[i + 1];
  }
  if (!result.input || !result.output) throw new Error('--input and --output are required.');
  return result;
}

function fitBeatLine(ticks) {
  if (ticks.length < 2) return null;
  const count = ticks.length;
  const meanIndex = (count - 1) / 2;
  const meanTime = ticks.reduce((sum, value) => sum + value, 0) / count;
  let covariance = 0;
  let variance = 0;
  for (let i = 0; i < count; i++) {
    covariance += (i - meanIndex) * (ticks[i] - meanTime);
    variance += (i - meanIndex) ** 2;
  }
  const periodSeconds = covariance / variance;
  const phaseSeconds = meanTime - periodSeconds * meanIndex;
  let squaredError = 0;
  let maxError = 0;
  for (let i = 0; i < count; i++) {
    const error = ticks[i] - (phaseSeconds + periodSeconds * i);
    squaredError += error * error;
    maxError = Math.max(maxError, Math.abs(error));
  }
  return {
    bpm: 60 / periodSeconds,
    periodSeconds,
    phaseSeconds,
    rmsResidualMs: 1000 * Math.sqrt(squaredError / count),
    maxResidualMs: 1000 * maxError,
  };
}

function selectTempo(rawBpm, ticks, fit) {
  const stableGrid = Boolean(fit && ticks.length >= 8 && fit.rmsResidualMs <= 20 && fit.maxResidualMs <= 50);
  const bpm = stableGrid ? fit.bpm : rawBpm;
  return {
    bpm,
    usedFittedBpm: stableGrid,
    stableGrid,
    periodSeconds: stableGrid ? fit.periodSeconds : 60 / rawBpm,
    phaseSeconds: stableGrid ? fit.phaseSeconds : ticks[0],
  };
}

async function main(argv) {
  const began = performance.now();
  const opts = options(argv);
  const root = path.resolve(__dirname, '..');
  const input = path.resolve(opts.input);
  const outputPath = path.resolve(opts.output);
  if (input.toLowerCase() === outputPath.toLowerCase()) throw new Error('Output must not replace the input audio.');
  if (!fs.statSync(input).isFile()) throw new Error('Input must be an audio file.');
  const moduleDir = path.join(root, 'runtime', 'essentia');
  const ffmpeg = path.join(root, 'assets', 'ffmpeg', 'ffmpeg.exe');
  const sampleRate = 44100;
  console.log('Decoding audio locally (mono 44100 Hz)...');
  const decoded = spawnSync(ffmpeg, [
    '-hide_banner', '-loglevel', 'error', '-i', input,
    '-vn', '-ac', '1', '-ar', String(sampleRate), '-f', 'f32le', 'pipe:1',
  ], { encoding: null, maxBuffer: 512 * 1024 * 1024, windowsHide: true });
  if (decoded.error) throw decoded.error;
  if (decoded.status !== 0) throw new Error(`FFmpeg failed (${decoded.status}): ${decoded.stderr.toString().trim()}`);
  if (!decoded.stdout.length || decoded.stdout.length % 4) throw new Error('Invalid decoded float PCM.');
  const samples = new Float32Array(decoded.stdout.buffer, decoded.stdout.byteOffset, decoded.stdout.length / 4);
  const audioDurationSeconds = samples.length / sampleRate;
  const decodeSeconds = (performance.now() - began) / 1000;

  const wasm = require(path.join(moduleDir, 'essentia-wasm.umd.js'));
  const Essentia = require(path.join(moduleDir, 'essentia.js-core.umd.js'));
  if (!wasm.calledRun) await new Promise(resolve => { wasm.onRuntimeInitialized = resolve; });
  const engine = new Essentia(wasm);
  if (typeof engine.RhythmExtractor2013 !== 'function' || typeof engine.algorithms.RhythmExtractor2013 !== 'function') {
    throw new Error('RhythmExtractor2013 is absent from the bundled runtime.');
  }
  let signal;
  let result;
  try {
    console.log('Detecting beats (RhythmExtractor2013, multifeature, 40..208 BPM)...');
    const analysisBegan = performance.now();
    signal = engine.arrayToVector(samples);
    result = engine.RhythmExtractor2013(signal, 208, 'multifeature', 40);
    const ticks = Array.from(engine.vectorToArray(result.ticks));
    if (!Number.isFinite(result.bpm) || result.bpm <= 0 || !Number.isFinite(result.confidence) || result.confidence < 0 || ticks.length < 2) {
      throw new Error('No usable beat/tempo estimate was found.');
    }
    for (let i = 0; i < ticks.length; i++) {
      if (!Number.isFinite(ticks[i]) || ticks[i] < 0 || ticks[i] > audioDurationSeconds || (i && ticks[i] <= ticks[i - 1])) {
        throw new Error('Beat tracking returned invalid beat times.');
      }
    }
    const fit = fitBeatLine(ticks);
    if (!fit || !Number.isFinite(fit.bpm) || fit.bpm <= 0 || !Number.isFinite(fit.phaseSeconds)) {
      throw new Error('Beat tracking returned an invalid fitted grid.');
    }
    const selected = selectTempo(result.bpm, ticks, fit);
    const report = {
      format: 'speaker-rhythm-analysis-v1',
      source: input,
      sourceBytes: fs.statSync(input).size,
      sourceSha256: crypto.createHash('sha256').update(fs.readFileSync(input)).digest('hex'),
      engine: 'Essentia.js 0.1.3 / RhythmExtractor2013',
      parameters: { sampleRate, method: 'multifeature', minTempo: 40, maxTempo: 208 },
      audioDurationSeconds,
      decodeSeconds,
      analysisSeconds: (performance.now() - analysisBegan) / 1000,
      rawBpm: result.bpm,
      fittedBpm: fit.bpm,
      ...selected,
      fittedPeriodSeconds: fit.periodSeconds,
      fittedPhaseSeconds: fit.phaseSeconds,
      rmsResidualMs: fit.rmsResidualMs,
      maxResidualMs: fit.maxResidualMs,
      confidence: result.confidence,
      confidenceIsProbability: false,
      beatCount: ticks.length,
      beatTicksSeconds: ticks,
      decoderWarnings: decoded.stderr.toString().trim(),
      limitations: [
        'Tempo and beat positions are estimates; half/double metrical tempo is possible.',
        'Confidence is not a probability or a calibrated quality percentage.',
        'Grid phase does not establish a downbeat, bar number or audio/MIDI alignment.',
        'Small residuals measure detected-grid consistency, not perceptual correctness.',
      ],
    };
    report.totalSeconds = (performance.now() - began) / 1000;
    fs.mkdirSync(path.dirname(outputPath), { recursive: true });
    fs.writeFileSync(outputPath, `${JSON.stringify(report, null, 2)}\n`);
    console.log(`BPM ${report.bpm.toFixed(2)} (${report.usedFittedBpm ? 'stable beat fit' : 'raw estimate'}); ${ticks.length} beats; RMS ${fit.rmsResidualMs.toFixed(2)} ms; max ${fit.maxResidualMs.toFixed(2)} ms.`);
    console.log(`Report: ${outputPath}`);
    return report;
  } finally {
    if (result) {
      result.ticks.delete();
      result.estimates.delete();
      result.bpmIntervals.delete();
    }
    if (signal) signal.delete();
  }
}

module.exports = { fitBeatLine, selectTempo, options, main };
if (require.main === module) {
  main(process.argv.slice(2)).catch(error => {
    console.error(error && error.message ? error.message : String(error));
    process.exitCode = 1;
  });
}
