// Sensibilidade do microfone (porta de ruído) com medidor, no fim da cadeia do microfone.
// Roda no thread de áudio (AudioWorklet). Mede o volume a cada 10 ms: acima do limite, a porta
// abre na hora; abaixo dele, espera um pouco (para não cortar o fim das palavras) e fecha com
// uma rampa curta, sem estalo. No modo automático o limite acompanha o ruído de fundo: o mínimo
// recente mais uma margem, como a sensibilidade automática do Discord.

const MIN_DB = -100;
const AUTO_MARGIN_DB = 12;
const AUTO_MIN_DB = -65;
const AUTO_MAX_DB = -30;
const MANUAL_MIN_DB = -80;
const MANUAL_MAX_DB = -10;
const HOLD_MS = 280;
const ATTACK_MS = 3;
const RELEASE_MS = 70;
const REPORT_EVERY_FRAMES = 5;

const clamp = (value, min, max) => Math.min(max, Math.max(min, value));

class DinoNoiseGate extends AudioWorkletProcessor {
  constructor(options) {
    super();
    this.frameSamples = Math.round(sampleRate / 100);
    this.attack = 1 - Math.exp(-1 / (sampleRate * ATTACK_MS / 1000));
    this.release = 1 - Math.exp(-1 / (sampleRate * RELEASE_MS / 1000));
    this.holdFrames = Math.round(HOLD_MS / 10);
    this.sum = 0;
    this.count = 0;
    this.gain = 1;
    this.target = 1;
    this.hold = 0;
    this.floor = -60;
    this.frames = 0;
    this.configure(options.processorOptions ?? {});
    this.port.onmessage = ({ data }) => {
      if (data?.type === "config") {
        this.configure(data);
      }
    };
  }

  configure({ auto = true, thresholdDb = -50 }) {
    this.auto = auto !== false;
    this.threshold = clamp(Number(thresholdDb) || -50, MANUAL_MIN_DB, MANUAL_MAX_DB);
  }

  process(inputs, outputs) {
    const input = inputs[0]?.[0];
    const output = outputs[0]?.[0];
    if (!output) {
      return true;
    }
    if (!input) {
      output.fill(0);
      return true;
    }
    for (let i = 0; i < output.length; i++) {
      const sample = input[i] ?? 0;
      this.sum += sample * sample;
      if (++this.count === this.frameSamples) {
        this.decide();
      }
      this.gain += (this.target - this.gain) * (this.target > this.gain ? this.attack : this.release);
      output[i] = sample * this.gain;
    }
    return true;
  }

  decide() {
    const rms = Math.sqrt(this.sum / this.count);
    this.sum = 0;
    this.count = 0;
    const level = Math.max(MIN_DB, 20 * Math.log10(Math.max(rms, 1e-5)));
    // Ruído de fundo: desce rápido, sobe devagar (2 dB/s), então a fala não o arrasta para cima.
    this.floor += level < this.floor ? (level - this.floor) * 0.3 : 0.02;
    const threshold = this.auto ? clamp(this.floor + AUTO_MARGIN_DB, AUTO_MIN_DB, AUTO_MAX_DB) : this.threshold;
    if (level > threshold) {
      this.hold = this.holdFrames;
      this.target = 1;
    } else if (this.hold > 0) {
      this.hold--;
    } else {
      this.target = 0;
    }
    if (++this.frames % REPORT_EVERY_FRAMES === 0) {
      this.port.postMessage({ type: "level", level, threshold, open: this.target === 1 });
    }
  }
}

registerProcessor("dino-noise-gate", DinoNoiseGate);
