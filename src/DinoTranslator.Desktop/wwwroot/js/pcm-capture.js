// Converte o áudio da chamada para o formato dos motores de fala: 16 kHz, mono, 16 bits.
// Roda no thread de áudio (AudioWorklet) e entrega blocos de 100 ms.
//
// Normalmente o AudioContext já roda em 16 kHz e o próprio navegador fez a conversão. Se ele
// rodar em outra taxa (48 kHz, 44,1 kHz), a conversão é feita aqui com um filtro passa-baixa de
// verdade antes de reduzir a taxa: sem ele, o "s", o "ch" e o "f" (que vão acima de 8 kHz)
// voltavam espelhados para dentro da faixa de voz e atrapalhavam o reconhecimento.

const OUTPUT_RATE = 16000;
const BLOCK_SAMPLES = OUTPUT_RATE / 10;
const TAPS = 96;
const CUTOFF_HZ = 7200;

/** Filtro sinc com janela de Blackman, ganho 1 na faixa de voz. */
function lowPass(inputRate) {
  const cutoff = CUTOFF_HZ / inputRate;
  const middle = (TAPS - 1) / 2;
  const taps = new Float32Array(TAPS);
  let sum = 0;
  for (let i = 0; i < TAPS; i++) {
    const x = i - middle;
    const sinc = x === 0 ? 2 * cutoff : Math.sin(2 * Math.PI * cutoff * x) / (Math.PI * x);
    const window = 0.42 - 0.5 * Math.cos((2 * Math.PI * i) / (TAPS - 1)) + 0.08 * Math.cos((4 * Math.PI * i) / (TAPS - 1));
    taps[i] = sinc * window;
    sum += taps[i];
  }
  for (let i = 0; i < TAPS; i++) {
    taps[i] /= sum;
  }
  return taps;
}

class PcmCapture extends AudioWorkletProcessor {
  constructor() {
    super();
    this.ratio = sampleRate / OUTPUT_RATE;
    this.filter = this.ratio > 1 ? lowPass(sampleRate) : null;
    this.history = new Float32Array(TAPS);
    this.cursor = 0;
    this.position = 0;
    this.block = new Int16Array(BLOCK_SAMPLES);
    this.length = 0;
  }

  process(inputs) {
    const channel = inputs[0]?.[0];
    if (!channel) {
      return true;
    }
    for (let i = 0; i < channel.length; i++) {
      if (!this.filter) {
        this.push(channel[i]);
        continue;
      }
      this.history[this.cursor] = channel[i];
      this.cursor = (this.cursor + 1) % TAPS;
      this.position += 1;
      if (this.position < this.ratio) {
        continue;
      }
      this.position -= this.ratio;
      // Só calcula o filtro nas amostras que ficam (uma a cada "ratio").
      let sample = 0;
      for (let k = 0; k < TAPS; k++) {
        sample += this.filter[k] * this.history[(this.cursor + k) % TAPS];
      }
      this.push(sample);
    }
    return true;
  }

  push(value) {
    const sample = Math.max(-1, Math.min(1, value));
    this.block[this.length++] = sample < 0 ? sample * 0x8000 : sample * 0x7fff;
    if (this.length === BLOCK_SAMPLES) {
      this.port.postMessage(this.block.buffer, [this.block.buffer]);
      this.block = new Int16Array(BLOCK_SAMPLES);
      this.length = 0;
    }
  }
}

registerProcessor("pcm-capture", PcmCapture);
