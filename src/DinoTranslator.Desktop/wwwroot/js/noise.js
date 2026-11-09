// Supressão de ruído do microfone: o que a pessoa escolhe (no teste de dispositivos ou durante a
// chamada) e a cadeia de áudio que limpa o microfone antes de ele seguir para a chamada e para a
// tradução.
//
// Microfone (cancelamento de eco e ganho do navegador) → supressor → sensibilidade (porta de
// ruído com medidor) → faixa enviada. Tudo roda neste computador, em AudioWorklets; os modelos
// vêm junto com o app (js/vendor, ver ORIGEM.json) e nada é baixado da internet.

import { createDeepFilterNetAudioWorklet } from "./vendor/deepfilternet/deepfilternet.js";
import { GtcrnWorkletNode, RnnoiseWorkletNode, loadGtcrn, loadRnnoise } from "./vendor/web-noise-suppressor/index.js";

const SAMPLE_RATE = 48000;
const STORAGE_KEY = "dt.ruido";
const VENDOR = new URL("./vendor/web-noise-suppressor/", import.meta.url);

/** Do mais leve ao mais forte. "navegador" é o supressor do próprio WebRTC (o de antes). */
export const NOISE_MODES = ["desligado", "navegador", "rnnoise", "gtcrn", "deepfilter"];

/** Se um modo falhar ao carregar ou pesar demais, a cadeia desce para o próximo desta lista. */
const FALLBACK = { deepfilter: "gtcrn", gtcrn: "rnnoise", rnnoise: "navegador" };

export const NOISE_DEFAULTS = Object.freeze({
  mode: "deepfilter",
  intensity: 60,
  autoSensitivity: true,
  sensitivityDb: -50,
  echoCancellation: true,
  autoGain: true,
});

const clampInt = (value, min, max, fallback) => {
  const number = Math.round(Number(value));
  return Number.isFinite(number) ? Math.min(max, Math.max(min, number)) : fallback;
};

/** Só valores conhecidos e dentro dos limites; o resto volta ao padrão. */
export function sanitizeNoise(value) {
  const settings = value && typeof value === "object" ? value : {};
  return {
    mode: NOISE_MODES.includes(settings.mode) ? settings.mode : NOISE_DEFAULTS.mode,
    intensity: clampInt(settings.intensity, 0, 100, NOISE_DEFAULTS.intensity),
    autoSensitivity: typeof settings.autoSensitivity === "boolean" ? settings.autoSensitivity : NOISE_DEFAULTS.autoSensitivity,
    sensitivityDb: clampInt(settings.sensitivityDb, -80, -10, NOISE_DEFAULTS.sensitivityDb),
    echoCancellation: typeof settings.echoCancellation === "boolean" ? settings.echoCancellation : NOISE_DEFAULTS.echoCancellation,
    autoGain: typeof settings.autoGain === "boolean" ? settings.autoGain : NOISE_DEFAULTS.autoGain,
  };
}

export function noiseSettings() {
  try {
    return sanitizeNoise(JSON.parse(localStorage.getItem(STORAGE_KEY) ?? "{}"));
  } catch {
    return { ...NOISE_DEFAULTS };
  }
}

export function saveNoiseSettings(value) {
  const settings = sanitizeNoise(value);
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(settings));
  } catch {
    // Sem armazenamento: vale só nesta sessão.
  }
  return settings;
}

/** Processamento do navegador: o supressor dele só fica ligado no modo "navegador". */
export function browserProcessing(settings, { highFidelity = false } = {}) {
  return {
    echoCancellation: !highFidelity && settings.echoCancellation,
    noiseSuppression: !highFidelity && settings.mode === "navegador",
    autoGainControl: !highFidelity && settings.autoGain,
  };
}

/** Quanto o DeepFilterNet3 pode abaixar: durante a fala e, pela porta dele, nas pausas. */
export function deepFilterAttenuation(intensity) {
  const speech = Math.round(8 + intensity * 0.32);
  return { speechAttenuationDb: speech, pauseAttenuationDb: speech + 20 };
}

// Os binários são lidos uma vez por sessão do app (o DeepFilterNet3 guarda os dele sozinho).
let rnnoiseBinary = null;
let gtcrnBinary = null;
const loadRnnoiseBinary = () => (rnnoiseBinary ??= loadRnnoise({ url: new URL("rnnoise.wasm", VENDOR).href, simdUrl: new URL("rnnoise_simd.wasm", VENDOR).href }).catch((error) => {
  rnnoiseBinary = null;
  throw error;
}));
const loadGtcrnBinary = () => (gtcrnBinary ??= loadGtcrn({ url: new URL("gtcrn.wasm", VENDOR).href }).catch((error) => {
  gtcrnBinary = null;
  throw error;
}));

/**
 * Cadeia do microfone. A faixa limpa (track) muda quando a cadeia é remontada (outro modo ou
 * outra intensidade): onTrack(nova, antiga) avisa quem a usa (o envio da chamada e a tradução).
 */
export class MicrophoneChain {
  constructor({ onTrack = null, onStatus = null, onLevel = null } = {}) {
    this.onTrack = onTrack;
    this.onStatus = onStatus;
    this.onLevel = onLevel;
    this.graph = null;
    this.settings = noiseSettings();
    this.status = { mode: this.settings.mode, problem: null };
    this.monitorSink = null;
    this.building = Promise.resolve();
  }

  get track() {
    return this.graph?.track ?? null;
  }

  /** Monta (ou remonta) a cadeia para este microfone com estas escolhas. */
  apply(rawTrack, settings = this.settings) {
    this.settings = sanitizeNoise(settings);
    // Uma montagem por vez: trocas seguidas (arrastar o controle) não se atropelam.
    this.building = this.building.catch(() => {}).then(() => this.build(rawTrack, this.settings));
    return this.building;
  }

  /** Só a sensibilidade mudou: ajusta a porta sem remontar nada. */
  setSensitivity(settings) {
    this.settings = sanitizeNoise({ ...this.settings, ...settings });
    this.graph?.gate.port.postMessage({ type: "config", auto: this.settings.autoSensitivity, thresholdDb: this.settings.sensitivityDb });
  }

  async build(rawTrack, settings) {
    const context = new AudioContext({ sampleRate: SAMPLE_RATE, latencyHint: "interactive" });
    let graph = null;
    try {
      await context.audioWorklet.addModule(new URL("./noise-gate.js", import.meta.url));
      const source = context.createMediaStreamSource(new MediaStream([rawTrack]));
      const gate = new AudioWorkletNode(context, "dino-noise-gate", {
        channelCount: 1,
        channelCountMode: "explicit",
        outputChannelCount: [1],
        processorOptions: { auto: settings.autoSensitivity, thresholdDb: settings.sensitivityDb },
      });
      gate.port.onmessage = ({ data }) => {
        if (data?.type === "level") {
          this.onLevel?.(data);
        }
      };
      const destination = context.createMediaStreamDestination();
      const { node: denoiser, mode, problem, dispose } = await this.createDenoiser(context, settings);
      if (denoiser) {
        source.connect(denoiser).connect(gate);
      } else {
        source.connect(gate);
      }
      gate.connect(destination);
      await context.resume();
      graph = { context, source, gate, denoiser, dispose, destination, track: destination.stream.getAudioTracks()[0], rawTrack };
      this.setStatus({ mode, problem });
    } catch (error) {
      await context.close().catch(() => {});
      throw error;
    }

    const previous = this.graph;
    this.graph = graph;
    if (this.monitorSink !== null) {
      await this.connectMonitor(this.monitorSink);
    }
    await this.onTrack?.(graph.track, previous?.track ?? null);
    if (previous) {
      this.teardown(previous);
    }
    return graph.track;
  }

  /** O supressor do modo pedido; se ele não carregar, o próximo mais leve. */
  async createDenoiser(context, settings) {
    let mode = settings.mode;
    let problem = null;
    while (mode === "deepfilter" || mode === "gtcrn" || mode === "rnnoise") {
      try {
        return { ...(await this.createModel(context, mode, settings)), mode, problem };
      } catch {
        problem = "unavailable";
        mode = FALLBACK[mode];
      }
    }
    return { node: null, dispose: null, mode, problem };
  }

  async createModel(context, mode, settings) {
    if (mode === "deepfilter") {
      const worklet = await createDeepFilterNetAudioWorklet(context, {
        ...deepFilterAttenuation(settings.intensity),
        pauseGateLookaheadFrames: 1,
        minSpeechFrames: 2,
        onOverload: () => this.overloaded(),
      });
      await worklet.ready;
      return { node: worklet.node, dispose: () => worklet.dispose() };
    }
    if (mode === "gtcrn") {
      const wasmBinary = await loadGtcrnBinary();
      await context.audioWorklet.addModule(new URL("gtcrn/workletProcessor.js", VENDOR).href);
      const node = new GtcrnWorkletNode(context, { wasmBinary, maxChannels: 1 });
      return { node, dispose: () => node.destroy() };
    }
    const wasmBinary = await loadRnnoiseBinary();
    await context.audioWorklet.addModule(new URL("rnnoise/workletProcessor.js", VENDOR).href);
    const node = new RnnoiseWorkletNode(context, { wasmBinary, maxChannels: 1 });
    return { node, dispose: () => node.destroy() };
  }

  /** O DeepFilterNet3 não está dando conta em tempo real: desce para o GTCRN, sem cortar a voz. */
  overloaded() {
    const graph = this.graph;
    if (!graph || this.status.mode !== "deepfilter") {
      return;
    }
    this.apply(graph.rawTrack, { ...this.settings, mode: FALLBACK.deepfilter })
      .then(() => this.setStatus({ mode: FALLBACK.deepfilter, problem: "overload" }))
      .catch(() => {});
  }

  setStatus(status) {
    this.status = status;
    this.onStatus?.(status);
  }

  /** Ouvir o próprio microfone já limpo (teste, com fones), na saída escolhida. */
  async monitor(on, sinkId = "") {
    this.monitorSink = on ? sinkId ?? "" : null;
    if (on) {
      await this.connectMonitor(this.monitorSink);
    } else {
      this.disconnectMonitor();
    }
  }

  async connectMonitor(sinkId) {
    const graph = this.graph;
    if (!graph) {
      return;
    }
    if (sinkId && typeof graph.context.setSinkId === "function") {
      await graph.context.setSinkId(sinkId).catch(() => {});
    }
    graph.gate.connect(graph.context.destination);
  }

  disconnectMonitor() {
    try {
      this.graph?.gate.disconnect(this.graph.context.destination);
    } catch {
      // Não estava ligado.
    }
  }

  teardown(graph) {
    graph.gate.port.onmessage = null;
    try {
      graph.source.disconnect();
      graph.denoiser?.disconnect();
      graph.gate.disconnect();
    } catch {
      // Já desligado.
    }
    graph.dispose?.();
    graph.track?.stop();
    // Fechar o contexto libera a memória dos modelos.
    graph.context.close().catch(() => {});
  }

  close() {
    this.onTrack = null;
    this.onLevel = null;
    this.onStatus = null;
    if (this.graph) {
      this.teardown(this.graph);
      this.graph = null;
    }
  }
}

/** Atualiza um medidor (barra) e a marca do limite direto no DOM, sem passar pelo C#. */
export function paintMeter(fill, mark, { level, threshold, open }) {
  const percent = (db) => Math.round(Math.min(100, Math.max(0, (db + 80) / 80 * 100)));
  if (fill) {
    fill.style.width = `${percent(level)}%`;
    fill.classList.toggle("meter__fill--loud", open);
  }
  if (mark) {
    mark.style.left = `${percent(threshold)}%`;
  }
}
