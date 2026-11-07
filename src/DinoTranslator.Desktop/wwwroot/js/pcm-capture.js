// Converte o áudio da chamada para o formato dos motores de fala: 16 kHz, mono, 16 bits.
// Roda no thread de áudio (AudioWorklet) e entrega blocos de 100 ms.

const OUTPUT_RATE = 16000;
const BLOCK_SAMPLES = OUTPUT_RATE / 10;

class PcmCapture extends AudioWorkletProcessor {
  constructor() {
    super();
    this.ratio = sampleRate / OUTPUT_RATE;
    this.position = 0;
    this.sum = 0;
    this.count = 0;
    this.block = new Int16Array(BLOCK_SAMPLES);
    this.length = 0;
  }

  process(inputs) {
    const channel = inputs[0]?.[0];
    if (!channel) {
      return true;
    }
    for (let i = 0; i < channel.length; i++) {
      // Média das amostras de cada intervalo: filtro passa-baixa simples, suficiente para voz.
      this.sum += channel[i];
      this.count++;
      this.position += 1;
      if (this.position < this.ratio) {
        continue;
      }
      this.position -= this.ratio;
      const sample = Math.max(-1, Math.min(1, this.sum / this.count));
      this.sum = 0;
      this.count = 0;
      this.block[this.length++] = sample < 0 ? sample * 0x8000 : sample * 0x7fff;
      if (this.length === BLOCK_SAMPLES) {
        this.port.postMessage(this.block.buffer, [this.block.buffer]);
        this.block = new Int16Array(BLOCK_SAMPLES);
        this.length = 0;
      }
    }
    return true;
  }
}

registerProcessor("pcm-capture", PcmCapture);
