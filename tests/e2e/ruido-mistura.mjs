// Monta o áudio do teste de ruído: 3 s só de ruído, a fala com ruído e mais 3 s só de ruído.
// Ruído = ventilador (ruído rosa) + teclado (cliques curtos), com a fala ~6 dB acima.
// Uso: node ruido-mistura.mjs <fala.wav 48 kHz mono 16 bits> <saída.wav>
import { readFileSync, writeFileSync } from "node:fs";

const [input, output] = process.argv.slice(2);
const RATE = 48000;

function readWav(path) {
  const data = readFileSync(path);
  let offset = 12;
  while (offset < data.length) {
    const id = data.toString("ascii", offset, offset + 4);
    const size = data.readUInt32LE(offset + 4);
    if (id === "data") {
      const samples = new Float32Array(size / 2);
      for (let i = 0; i < samples.length; i++) {
        samples[i] = data.readInt16LE(offset + 8 + i * 2) / 32768;
      }
      return samples;
    }
    offset += 8 + size + (size % 2);
  }
  throw new Error("WAV sem dados");
}

function writeWav(path, samples) {
  const data = Buffer.alloc(44 + samples.length * 2);
  data.write("RIFF", 0);
  data.writeUInt32LE(36 + samples.length * 2, 4);
  data.write("WAVEfmt ", 8);
  data.writeUInt32LE(16, 16);
  data.writeUInt16LE(1, 20);
  data.writeUInt16LE(1, 22);
  data.writeUInt32LE(RATE, 24);
  data.writeUInt32LE(RATE * 2, 28);
  data.writeUInt16LE(2, 32);
  data.writeUInt16LE(16, 34);
  data.write("data", 36);
  data.writeUInt32LE(samples.length * 2, 40);
  for (let i = 0; i < samples.length; i++) {
    data.writeInt16LE(Math.round(Math.max(-1, Math.min(1, samples[i])) * 32767), 44 + i * 2);
  }
  writeFileSync(path, data);
}

const rms = (x) => Math.sqrt(x.reduce((sum, v) => sum + v * v, 0) / x.length);

// Gerador pseudoaleatório fixo: o mesmo ruído a cada execução.
let seed = 12345;
const random = () => ((seed = (seed * 1103515245 + 12345) & 0x7fffffff) / 0x7fffffff) * 2 - 1;

const speech = readWav(input);
const total = RATE * 3 + speech.length + RATE * 3;
const fan = new Float32Array(total);
// Ruído rosa (filtro de Paul Kellet).
let b0 = 0, b1 = 0, b2 = 0, b3 = 0, b4 = 0, b5 = 0, b6 = 0;
for (let i = 0; i < total; i++) {
  const white = random();
  b0 = 0.99886 * b0 + white * 0.0555179; b1 = 0.99332 * b1 + white * 0.0750759; b2 = 0.969 * b2 + white * 0.153852;
  b3 = 0.8665 * b3 + white * 0.3104856; b4 = 0.55 * b4 + white * 0.5329522; b5 = -0.7616 * b5 - white * 0.016898;
  fan[i] = (b0 + b1 + b2 + b3 + b4 + b5 + b6 + white * 0.5362) * 0.11;
  b6 = white * 0.115926;
}
const keyboard = new Float32Array(total);
for (let at = 0; at < total; at += Math.round(RATE * (0.12 + 0.13 * Math.abs(random())))) {
  for (let j = 0; j < RATE * 0.025 && at + j < total; j++) {
    keyboard[at + j] += random() * Math.exp(-j / (RATE * 0.004));
  }
}
const speechRms = rms(speech.filter((v) => Math.abs(v) > 0.01));
const noise = new Float32Array(total);
const fanRms = rms(fan);
const keyRms = rms(keyboard);
for (let i = 0; i < total; i++) {
  noise[i] = fan[i] / fanRms * 0.6 + keyboard[i] / keyRms * 0.4;
}
// Fala ~6 dB acima do ruído.
const noiseGain = speechRms / rms(noise) / 2;
const mix = new Float32Array(total);
for (let i = 0; i < total; i++) {
  const voice = i >= RATE * 3 && i < RATE * 3 + speech.length ? speech[i - RATE * 3] : 0;
  mix[i] = voice + noise[i] * noiseGain;
}
const peak = mix.reduce((max, v) => Math.max(max, Math.abs(v)), 0);
writeWav(output, mix.map((v) => v / peak * 0.8));
console.log(`fala ${(speech.length / RATE).toFixed(1)} s, total ${(total / RATE).toFixed(1)} s`);
