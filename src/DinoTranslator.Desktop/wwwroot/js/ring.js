// Toque da chamada pela conta, gerado na hora (sem arquivo de áudio):
//   entrada: dois toques curtos a cada 3 s, como um telefone;
//   saida: "chamando", um tom de 425 Hz por 1 s a cada 5 s (o de chamada das operadoras no Brasil).
const PATTERNS = {
  entrada: { every: 3000, volume: 0.12, notes: [[0, 0.35, [660, 880]], [0.5, 0.35, [660, 880]]] },
  saida: { every: 5000, volume: 0.05, notes: [[0, 1, [425]]] },
};

let context = null;
let timer = null;

export function start(kind) {
  stop();
  const pattern = PATTERNS[kind];
  if (!pattern) {
    return false;
  }
  try {
    context = new AudioContext();
  } catch {
    return false;
  }
  const play = () => {
    if (!context) {
      return;
    }
    const now = context.currentTime + 0.02;
    for (const [at, length, frequencies] of pattern.notes) {
      const gain = context.createGain();
      gain.gain.setValueAtTime(0, now + at);
      gain.gain.linearRampToValueAtTime(pattern.volume, now + at + 0.02);
      gain.gain.setValueAtTime(pattern.volume, now + at + length - 0.04);
      gain.gain.linearRampToValueAtTime(0, now + at + length);
      gain.connect(context.destination);
      for (const frequency of frequencies) {
        const tone = context.createOscillator();
        tone.type = "sine";
        tone.frequency.value = frequency;
        tone.connect(gain);
        tone.start(now + at);
        tone.stop(now + at + length + 0.01);
      }
    }
  };
  context.resume?.().catch(() => {});
  play();
  timer = setInterval(play, pattern.every);
  return true;
}

export function stop() {
  if (timer) {
    clearInterval(timer);
    timer = null;
  }
  if (context) {
    context.close().catch(() => {});
    context = null;
  }
}
