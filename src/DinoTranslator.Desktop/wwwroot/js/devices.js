// Teste de câmera, microfone, alto-falante e tradução antes da chamada. As escolhas ficam
// guardadas como preferência e a chamada usa as mesmas (call.js).
import { QUALITIES, mediaConstraints, preference, setPreference, showPreview, speechContext, videoQuality } from "./call.js";
import { MicrophoneChain, noiseSettings, paintMeter, saveNoiseSettings } from "./noise.js";

let state = null;

/**
 * Liga a prévia da câmera e o microfone já com o supressor de ruído escolhido; o medidor mostra
 * o volume depois da limpeza e a marca, o limite da sensibilidade. Devolve o que ficou ligado.
 */
export async function startPreview(video, meter, mark) {
  stopPreview();
  let stream;
  let cameraError = null;
  try {
    stream = await navigator.mediaDevices.getUserMedia(mediaConstraints());
  } catch (error) {
    cameraError = error?.name ?? "erro";
    stream = await navigator.mediaDevices.getUserMedia({ audio: mediaConstraints().audio }).catch(() => null);
  }
  if (!stream) {
    return { camera: false, microphone: false, width: 0, height: 0, frameRate: 0, cameraError };
  }
  state = { stream, video, meter, mark, context: null, timer: null, capture: null, chain: null, status: null, monitoring: false };
  showPreview(video, stream);

  const audio = stream.getAudioTracks()[0];
  if (audio) {
    state.context = speechContext();
    const chain = new MicrophoneChain({
      onLevel: (level) => paintMeter(meter, mark, level),
      onStatus: (status) => {
        if (state) {
          state.status = status;
        }
      },
    });
    try {
      await chain.apply(audio);
      state.chain = chain;
    } catch {
      chain.close();
    }
  }
  if (audio && !state.chain) {
    // Sem a cadeia do supressor: medidor simples do microfone direto.
    const context = state.context;
    const analyser = context.createAnalyser();
    analyser.fftSize = 1024;
    context.createMediaStreamSource(new MediaStream([audio])).connect(analyser);
    const samples = new Float32Array(analyser.fftSize);
    // O medidor é atualizado aqui mesmo, sem ir e voltar ao C#.
    state.timer = setInterval(() => {
      analyser.getFloatTimeDomainData(samples);
      let peak = 0;
      for (const sample of samples) {
        peak = Math.max(peak, Math.abs(sample));
      }
      const level = Math.min(100, Math.round(Math.sqrt(peak) * 100));
      meter.style.width = `${level}%`;
      meter.classList.toggle("meter__fill--loud", level > 35);
    }, 60);
  }

  const settings = stream.getVideoTracks()[0]?.getSettings() ?? {};
  return {
    camera: Boolean(stream.getVideoTracks()[0]),
    microphone: Boolean(audio),
    width: settings.width ?? 0,
    height: settings.height ?? 0,
    frameRate: Math.round(settings.frameRate ?? 0),
    cameraError,
  };
}

/** Dispositivos disponíveis e os escolhidos (os nomes só aparecem depois da permissão). */
export async function listDevices() {
  const devices = await navigator.mediaDevices.enumerateDevices();
  const current = (kind) => state?.stream.getTracks().find((t) => t.kind === kind)?.getSettings().deviceId ?? null;
  return {
    cameras: devices.filter((d) => d.kind === "videoinput").map((d) => ({ id: d.deviceId, label: d.label || "Câmera" })),
    microphones: devices.filter((d) => d.kind === "audioinput" && d.deviceId !== "communications").map((d) => ({ id: d.deviceId, label: d.label || "Microfone" })),
    speakers: devices.filter((d) => d.kind === "audiooutput" && d.deviceId !== "communications").map((d) => ({ id: d.deviceId, label: d.label || "Alto-falante" })),
    camera: current("video") ?? preference("camera"),
    microphone: current("audio") ?? preference("microphone"),
    speaker: preference("speaker") ?? "default",
    quality: videoQuality(),
  };
}

/** Guarda uma escolha (camera, microphone, speaker ou quality). */
export function choose(kind, value) {
  if (kind === "quality" && !Object.hasOwn(QUALITIES, value)) {
    return;
  }
  setPreference(kind, value);
}

/** Supressor de ruído: as escolhas guardadas e o modo que está rodando na prévia. */
export function noiseState() {
  return {
    settings: state?.chain?.settings ?? noiseSettings(),
    mode: state?.status?.mode ?? null,
    problem: state?.status?.problem ?? null,
  };
}

/**
 * Aplica as escolhas de ruído na prévia. Devolve true quando o microfone precisa ser aberto de
 * novo (mudou o processamento do próprio navegador): aí o C# reinicia a prévia.
 */
export async function setNoise(value) {
  const before = state?.chain?.settings ?? noiseSettings();
  const settings = saveNoiseSettings(value);
  const chain = state?.chain;
  const raw = state?.stream.getAudioTracks()[0];
  if (!chain || !raw) {
    return false;
  }
  if (before.echoCancellation !== settings.echoCancellation || before.autoGain !== settings.autoGain
      || (before.mode === "navegador") !== (settings.mode === "navegador")) {
    return true;
  }
  if (before.mode !== settings.mode || chain.status.mode !== settings.mode
      || (settings.mode === "deepfilter" && before.intensity !== settings.intensity)) {
    await chain.apply(raw, settings);
    rewireTranslationTest();
  } else {
    chain.setSensitivity(settings);
  }
  return false;
}

/** Ouvir o próprio microfone já limpo (com fones, para não microfonar). */
export async function monitorMicrophone(on) {
  if (!state?.chain) {
    return false;
  }
  state.monitoring = on;
  await state.chain.monitor(on, preference("speaker") ?? "");
  return on;
}

/** Toca um "plim" curto na saída escolhida. */
export async function playTestSound() {
  const context = new AudioContext();
  const speaker = preference("speaker");
  if (speaker && typeof context.setSinkId === "function") {
    await context.setSinkId(speaker).catch(() => {});
  }
  const now = context.currentTime;
  for (const [offset, frequency] of [[0, 880], [0.18, 1320]]) {
    const oscillator = context.createOscillator();
    const gain = context.createGain();
    oscillator.frequency.value = frequency;
    gain.gain.setValueAtTime(0, now + offset);
    gain.gain.linearRampToValueAtTime(0.25, now + offset + 0.02);
    gain.gain.exponentialRampToValueAtTime(0.001, now + offset + 0.5);
    oscillator.connect(gain).connect(context.destination);
    oscillator.start(now + offset);
    oscillator.stop(now + offset + 0.55);
  }
  setTimeout(() => context.close(), 1200);
}

/** Manda a fala do microfone (16 kHz) para o C# testar o reconhecimento e a tradução. */
export async function startTranslationTest(dotnet) {
  const audio = state?.chain?.track ?? state?.stream.getAudioTracks()[0];
  if (!state?.context || !audio || state.capture) {
    return false;
  }
  await state.context.audioWorklet.addModule(new URL("./pcm-capture.js", import.meta.url));
  const source = state.context.createMediaStreamSource(new MediaStream([audio]));
  const node = new AudioWorkletNode(state.context, "pcm-capture");
  const silent = state.context.createGain();
  silent.gain.value = 0;
  node.port.onmessage = ({ data }) => dotnet.invokeMethodAsync("OnTestAudio", new Uint8Array(data));
  source.connect(node);
  node.connect(silent).connect(state.context.destination);
  await state.context.resume();
  state.capture = { source, node, silent };
  return true;
}

/** A cadeia foi remontada (outra faixa limpa): o teste da tradução passa a ouvir a nova. */
function rewireTranslationTest() {
  const track = state?.chain?.track;
  if (!state?.capture || !track) {
    return;
  }
  state.capture.source.disconnect();
  state.capture.source = state.context.createMediaStreamSource(new MediaStream([track]));
  state.capture.source.connect(state.capture.node);
}

export function stopTranslationTest() {
  if (!state?.capture) {
    return;
  }
  const { source, node, silent } = state.capture;
  node.port.onmessage = null;
  source.disconnect();
  node.disconnect();
  silent.disconnect();
  state.capture = null;
}

export function stopPreview() {
  if (!state) {
    return;
  }
  stopTranslationTest();
  clearInterval(state.timer);
  state.chain?.close();
  state.context?.close();
  for (const track of state.stream.getTracks()) {
    track.stop();
  }
  state.video.srcObject = null;
  state.meter.style.width = "0%";
  state.meter.classList.remove("meter__fill--loud");
  state = null;
}
