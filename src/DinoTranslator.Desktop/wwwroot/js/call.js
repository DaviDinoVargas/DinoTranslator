// Camada de mídia da chamada: WebRTC do WebView2.
// O código de convite/resposta só abre os canais de dados; câmera, áudio e tela são
// negociados depois pelo canal "sinal", por dentro da conexão já cifrada (DTLS).
// A lógica do app (códigos, perfil, mensagens) fica em C#.

import { MicrophoneChain, browserProcessing, noiseSettings, paintMeter, saveNoiseSettings } from "./noise.js";

const ICE_SERVERS = [
  { urls: ["stun:stun.l.google.com:19302", "stun:stun1.l.google.com:19302"] },
  { urls: "stun:stun.cloudflare.com:3478" },
];
const GATHER_TIMEOUT_MS = 4000;
const STATS_INTERVAL_MS = 1000;
// O ping mostrado é a mediana dos últimos segundos: uma medida isolada salta quando um quadro
// grande de vídeo passa na frente, e não diz como a conversa está.
const RTT_WINDOW = 5;
const MAX_SIGNAL_LENGTH = 64 * 1024;
const MAX_APP_MESSAGE_LENGTH = 16 * 1024;
const AUDIO_MAX_BITRATE = 128_000;

/** Qualidade da câmera enviada; o WebRTC ainda reduz sozinho se a rede apertar. */
export const QUALITIES = {
  maxima: { width: 1920, height: 1080, frameRate: 30, bitrate: 4_000_000 },
  alta: { width: 1280, height: 720, frameRate: 30, bitrate: 2_000_000 },
  media: { width: 854, height: 480, frameRate: 30, bitrate: 900_000 },
  baixa: { width: 640, height: 360, frameRate: 24, bitrate: 450_000 },
};

// Preferências deste computador (câmera, microfone, alto-falante e qualidade), guardadas no
// próprio WebView2 do app e usadas pelo teste de dispositivos e pela chamada.
const PREFERENCES = { camera: "dt.camera", microphone: "dt.microfone", speaker: "dt.altofalante", quality: "dt.qualidade" };

export function preference(name) {
  try {
    return localStorage.getItem(PREFERENCES[name]) || null;
  } catch {
    return null;
  }
}

export function setPreference(name, value) {
  try {
    if (value) {
      localStorage.setItem(PREFERENCES[name], value);
    } else {
      localStorage.removeItem(PREFERENCES[name]);
    }
  } catch {
    // Sem armazenamento: vale só nesta sessão.
  }
}

export function videoQuality() {
  const name = preference("quality");
  return Object.hasOwn(QUALITIES, name ?? "") ? name : "maxima";
}

/** Restrições de câmera e microfone com os dispositivos escolhidos (se ainda existirem). */
export function mediaConstraints(quality = videoQuality(), { highFidelity = false } = {}) {
  const { width, height, frameRate } = QUALITIES[quality];
  const camera = preference("camera");
  const microphone = preference("microphone");
  return {
    audio: {
      ...(microphone ? { deviceId: { ideal: microphone } } : {}),
      // O supressor de ruído escolhido roda depois, na cadeia do microfone (noise.js); o do
      // navegador só fica ligado se for ele o escolhido.
      ...browserProcessing(noiseSettings(), { highFidelity }),
      sampleRate: 48000,
      channelCount: 1,
    },
    video: {
      ...(camera ? { deviceId: { ideal: camera } } : {}),
      width: { ideal: width },
      height: { ideal: height },
      frameRate: { ideal: frameRate, max: 30 },
    },
  };
}

/**
 * Mostra a própria câmera só com a imagem. O atributo "muted" do HTML não basta: o Chromium só o
 * respeita quando vem do HTML lido da página, não quando o Blazor cria o elemento — e aí o vídeo
 * tocava o som do próprio microfone (a pessoa se ouvia, e o microfone captava de novo: eco).
 */
export function showPreview(element, stream) {
  element.muted = true;
  element.defaultMuted = true;
  element.srcObject = stream ? new MediaStream(stream.getVideoTracks()) : null;
}

/**
 * Áudio para o reconhecimento já em 16 kHz: o próprio navegador converte a fala com o filtro
 * dele, de boa qualidade. Se não der, a captura (pcm-capture.js) filtra e converte sozinha.
 */
export function speechContext() {
  try {
    return new AudioContext({ sampleRate: 16000 });
  } catch {
    return new AudioContext();
  }
}

/** Saída de som escolhida no teste de dispositivos (alto-falante ou fone). */
export async function applySpeaker(element) {
  const speaker = preference("speaker");
  if (speaker && typeof element?.setSinkId === "function") {
    try {
      await element.setSinkId(speaker);
    } catch {
      // Dispositivo removido: fica a saída padrão do Windows.
    }
  }
}
const SCREEN_MAX = { width: 2560, height: 1440, frameRate: 30 };
// Tela "nítida" guarda a resolução (texto, código); "fluida" guarda os quadros por segundo (vídeo).
const SCREEN_BITRATE = { detail: 6_000_000, motion: 8_000_000 };
const SCREEN_CODECS = ["video/AV1", "video/VP9", "video/H264"];
const MAX_STREAM_ID_LENGTH = 64;

export class CallPeer {
  /**
   * @param {object} events callbacks: onState, onStats, onAppMessage, onChannelOpen, onRemoteStream,
   *   onRemoteScreen, onLocalScreen, onScreenStats, onError
   * @param {{polite: boolean, relay: RTCIceServer | null}} options o lado "educado" cede quando os dois
   *   renegociam ao mesmo tempo; relay é a retransmissão (TURN) opcional
   */
  constructor(events, { polite, relay = null }) {
    this.events = events;
    this.polite = polite;
    this.makingOffer = false;
    this.ignoreOffer = false;
    this.pendingNegotiation = false;
    this.remoteFingerprint = null;
    this.localStream = null;
    this.highFidelity = false;
    // Microfone limpo (supressor + sensibilidade) e o envio dele na chamada.
    this.microphone = null;
    this.audioSender = null;
    this.noiseLevel = null;
    this.quality = videoQuality();
    this.remoteStream = new MediaStream();
    this.statsTimer = null;
    this.rttSamples = [];

    // Tela compartilhada: um transceptor só para ela, reaproveitado a cada compartilhamento,
    // e um fluxo de id fixo que o outro lado usa para separar a tela da câmera.
    this.screenTransceiver = null;
    this.screenStream = new MediaStream();
    this.screenTrack = null;
    this.screenMode = "detail";
    this.remoteScreenStreamId = null;
    this.remoteScreen = new MediaStream();
    this.remoteScreenReceiver = null;
    // Prévia da própria tela, para a pessoa ver o que está mostrando.
    this.localScreenView = null;

    // Captura da fala para a tradução (só no computador que traduz).
    this.audioContext = null;
    this.silentSink = null;
    this.captures = new Map();
    this.onSpeechAudio = null;

    const iceServers = relay ? [...ICE_SERVERS, relay] : ICE_SERVERS;
    this.pc = new RTCPeerConnection({ iceServers, bundlePolicy: "max-bundle", rtcpMuxPolicy: "require" });
    // Canais com id fixo: existem dos dois lados sem troca extra de sinais.
    this.app = this.pc.createDataChannel("app", { negotiated: true, id: 0, ordered: true });
    this.signal = this.pc.createDataChannel("sinal", { negotiated: true, id: 1, ordered: true });

    this.app.onopen = () => this.events.onChannelOpen?.();
    this.app.onmessage = ({ data }) => {
      if (typeof data === "string" && data.length <= MAX_APP_MESSAGE_LENGTH) {
        this.events.onAppMessage?.(data);
      }
    };
    this.signal.onopen = () => {
      if (this.pendingNegotiation) {
        this.pendingNegotiation = false;
        this.negotiate();
      }
    };
    this.signal.onmessage = ({ data }) => this.onSignal(data);

    this.pc.onnegotiationneeded = () => this.negotiate();
    this.pc.onicecandidate = ({ candidate }) => {
      // Na abertura os candidatos vão dentro do código; depois, pelo canal de sinal.
      if (candidate && this.signal.readyState === "open") {
        this.sendSignal({ candidate: candidate.toJSON() });
      }
    };
    this.pc.ontrack = ({ track, streams, receiver }) => {
      if (streams[0]?.id && streams[0].id === this.remoteScreenStreamId) {
        for (const previous of this.remoteScreen.getTracks()) {
          this.remoteScreen.removeTrack(previous);
        }
        this.remoteScreen.addTrack(track);
        this.remoteScreenReceiver = receiver;
        this.events.onRemoteScreen?.(true);
        return;
      }
      this.remoteStream.addTrack(track);
      track.onended = () => this.remoteStream.removeTrack(track);
      if (track.kind === "audio") {
        this.attachSpeechCapture("remote", track);
      }
      this.events.onRemoteStream?.(this.remoteStream, receiver);
    };
    this.pc.onconnectionstatechange = () => {
      const state = this.pc.connectionState;
      this.events.onState?.(state);
      if (state === "connected") {
        this.startStats();
        this.tuneSenders();
      }
      if (state === "failed" || state === "closed") {
        this.stopStats();
      }
    };
    this.pc.onsignalingstatechange = () => {
      if (this.pc.signalingState === "stable") {
        this.tuneSenders();
      }
    };
  }

  // ---- Abertura pelo código -------------------------------------------------

  async createInvite() {
    const offer = await this.pc.createOffer();
    await this.pc.setLocalDescription(offer);
    await this.waitForIceGathering();
    return this.pc.localDescription.sdp;
  }

  async acceptInvite(sdp) {
    this.remoteFingerprint = fingerprintOf(sdp);
    await this.pc.setRemoteDescription({ type: "offer", sdp });
    const answer = await this.pc.createAnswer();
    await this.pc.setLocalDescription(answer);
    await this.waitForIceGathering();
    return this.pc.localDescription.sdp;
  }

  async acceptAnswer(sdp) {
    this.remoteFingerprint = fingerprintOf(sdp);
    await this.pc.setRemoteDescription({ type: "answer", sdp });
  }

  localSdp() {
    return this.pc.localDescription?.sdp ?? null;
  }

  waitForIceGathering() {
    if (this.pc.iceGatheringState === "complete") {
      return Promise.resolve();
    }
    return new Promise((resolve) => {
      const done = () => {
        clearTimeout(timer);
        this.pc.removeEventListener("icegatheringstatechange", onChange);
        resolve();
      };
      const onChange = () => {
        if (this.pc.iceGatheringState === "complete") {
          done();
        }
      };
      // Um STUN que não responde não pode travar a criação do código.
      const timer = setTimeout(done, GATHER_TIMEOUT_MS);
      this.pc.addEventListener("icegatheringstatechange", onChange);
    });
  }

  // ---- Renegociação por dentro da conexão ("perfect negotiation") -----------

  async negotiate() {
    if (this.signal.readyState !== "open") {
      // Ainda na abertura pelo código: renegocia assim que o canal de sinal abrir.
      this.pendingNegotiation = this.remoteFingerprint !== null;
      return;
    }
    try {
      this.makingOffer = true;
      const offer = await this.pc.createOffer();
      if (this.pc.signalingState !== "stable") {
        return;
      }
      offer.sdp = tuneOpus(offer.sdp);
      await this.pc.setLocalDescription(offer);
      this.sendSignal({ description: { type: offer.type, sdp: this.pc.localDescription.sdp } });
    } catch (error) {
      this.events.onError?.(String(error));
    } finally {
      this.makingOffer = false;
    }
  }

  async onSignal(data) {
    if (typeof data !== "string" || data.length > MAX_SIGNAL_LENGTH) {
      return;
    }
    let message;
    try {
      message = JSON.parse(data);
    } catch {
      return;
    }

    try {
      const description = message?.description;
      if (description && (description.type === "offer" || description.type === "answer") && typeof description.sdp === "string") {
        // O certificado do outro lado não pode mudar no meio da chamada.
        if (fingerprintOf(description.sdp) !== this.remoteFingerprint) {
          this.events.onError?.("Renegociação com outro certificado recusada.");
          return;
        }
        const collision = description.type === "offer" && (this.makingOffer || this.pc.signalingState !== "stable");
        this.ignoreOffer = !this.polite && collision;
        if (this.ignoreOffer) {
          return;
        }
        await this.pc.setRemoteDescription(description);
        if (description.type === "offer") {
          const answer = await this.pc.createAnswer();
          answer.sdp = tuneOpus(answer.sdp);
          await this.pc.setLocalDescription(answer);
          this.sendSignal({ description: { type: answer.type, sdp: this.pc.localDescription.sdp } });
        }
      } else if (message && Object.hasOwn(message, "screen")) {
        this.onRemoteScreenSignal(message.screen);
      } else if (message?.candidate && typeof message.candidate === "object") {
        try {
          await this.pc.addIceCandidate(message.candidate);
        } catch (error) {
          if (!this.ignoreOffer) {
            throw error;
          }
        }
      }
    } catch (error) {
      this.events.onError?.(String(error));
    }
  }

  /** O outro lado avisa, antes da mídia, qual fluxo é a tela; null = parou de compartilhar. */
  onRemoteScreenSignal(screen) {
    if (screen === null) {
      this.remoteScreenStreamId = null;
      this.remoteScreenReceiver = null;
      this.events.onRemoteScreen?.(false);
    } else if (typeof screen?.streamId === "string" && screen.streamId.length <= MAX_STREAM_ID_LENGTH) {
      this.remoteScreenStreamId = screen.streamId;
    }
  }

  sendSignal(message) {
    if (this.signal.readyState === "open") {
      this.signal.send(JSON.stringify(message));
    }
  }

  // ---- Mídia ------------------------------------------------------------------

  /** Câmera e microfone. highFidelity desliga o processamento de áudio (exige fones). */
  async startLocalMedia({ highFidelity = false } = {}) {
    this.highFidelity = highFidelity;
    let stream;
    try {
      stream = await navigator.mediaDevices.getUserMedia(mediaConstraints(this.quality, { highFidelity }));
    } catch {
      // Sem câmera (ou ocupada por outro programa): a chamada segue só com o microfone.
      stream = await navigator.mediaDevices.getUserMedia({ audio: mediaConstraints(this.quality, { highFidelity }).audio });
    }
    await this.addStream(stream);
    return stream;
  }

  /** Troca a qualidade da câmera no meio da chamada, sem renegociar. */
  async setVideoQuality(name) {
    if (!Object.hasOwn(QUALITIES, name)) {
      return;
    }
    this.quality = name;
    setPreference("quality", name);
    const { width, height, frameRate } = QUALITIES[name];
    for (const track of this.localStream?.getVideoTracks() ?? []) {
      try {
        await track.applyConstraints({ width: { ideal: width }, height: { ideal: height }, frameRate: { ideal: frameRate, max: 30 } });
      } catch {
        // A câmera não aceita esse tamanho: fica no mais próximo e só o limite de taxa muda.
      }
    }
    await this.tuneSenders();
  }

  hasLocalTrack(kind) {
    return (this.localStream?.getTracks() ?? []).some((track) => track.kind === kind && track.readyState === "live");
  }

  async addStream(stream) {
    this.localStream = stream;
    for (const track of stream.getVideoTracks()) {
      track.contentHint = "motion";
      this.pc.addTrack(track, stream);
    }
    const microphone = stream.getAudioTracks()[0];
    if (microphone) {
      await this.attachMicrophone(microphone, stream);
    }
  }

  /**
   * O microfone vai para a chamada (e para a tradução) já limpo pela cadeia do supressor. Se a
   * cadeia não montar, segue o microfone direto, como antes.
   */
  async attachMicrophone(raw, stream) {
    this.microphone = new MicrophoneChain({
      onTrack: (track) => this.useMicrophoneTrack(track, stream),
      onStatus: (status) => this.events.onNoiseStatus?.(status),
      onLevel: (level) => this.noiseLevel?.(level),
    });
    try {
      await this.microphone.apply(raw);
    } catch {
      this.microphone.close();
      this.microphone = null;
      await this.useMicrophoneTrack(raw, stream);
    }
  }

  async useMicrophoneTrack(track, stream) {
    if (this.audioSender) {
      await this.audioSender.replaceTrack(track);
    } else {
      this.audioSender = this.pc.addTrack(track, stream);
    }
    this.detachSpeechCapture("local");
    this.attachSpeechCapture("local", track);
  }

  /**
   * Troca o supressor ou o ajuste dele no meio da chamada, sem renegociar: só a sensibilidade
   * muda na hora; o resto remonta a cadeia (e, se o processamento do navegador mudou, pega o
   * microfone de novo com as novas opções).
   */
  async setNoise(value) {
    const before = this.microphone?.settings ?? noiseSettings();
    const settings = saveNoiseSettings(value);
    const raw = this.localStream?.getAudioTracks()[0];
    if (!this.microphone || !raw) {
      return settings;
    }
    const newRaw = before.echoCancellation !== settings.echoCancellation
      || before.autoGain !== settings.autoGain
      || (before.mode === "navegador") !== (settings.mode === "navegador");
    const rebuild = newRaw
      || before.mode !== settings.mode
      || this.microphone.status.mode !== settings.mode
      || (settings.mode === "deepfilter" && before.intensity !== settings.intensity);
    if (!rebuild) {
      this.microphone.setSensitivity(settings);
      return settings;
    }
    await this.microphone.apply(newRaw ? await this.reacquireMicrophone(raw) : raw, settings);
    return settings;
  }

  async reacquireMicrophone(old) {
    const stream = await navigator.mediaDevices.getUserMedia({ audio: mediaConstraints(this.quality, { highFidelity: this.highFidelity }).audio });
    const track = stream.getAudioTracks()[0];
    track.enabled = old.enabled;
    this.localStream.removeTrack(old);
    this.localStream.addTrack(track);
    old.stop();
    return track;
  }

  setTrackEnabled(kind, enabled) {
    for (const track of this.localStream?.getTracks() ?? []) {
      if (track.kind === kind) {
        track.enabled = enabled;
      }
    }
  }

  // ---- Fala para a tradução ------------------------------------------------------------

  /** Liga a captura das duas falas em 16 kHz; onAudio(who, Uint8Array), who = "local" ou "remote". */
  async enableSpeechCapture(onAudio) {
    this.onSpeechAudio = onAudio;
    if (!this.audioContext) {
      this.audioContext = speechContext();
      await this.audioContext.audioWorklet.addModule(new URL("./pcm-capture.js", import.meta.url));
      // Mantém o grafo de áudio ativo sem tocar nada de novo: saída com volume zero.
      this.silentSink = this.audioContext.createGain();
      this.silentSink.gain.value = 0;
      this.silentSink.connect(this.audioContext.destination);
    }
    await this.audioContext.resume();
    this.attachSpeechCapture("local", this.microphone?.track ?? this.localStream?.getAudioTracks()[0]);
    this.attachSpeechCapture("remote", this.remoteStream.getAudioTracks()[0]);
  }

  attachSpeechCapture(who, track) {
    if (!this.audioContext || !this.silentSink || !this.onSpeechAudio || !track || this.captures.has(who)) {
      return;
    }
    const source = this.audioContext.createMediaStreamSource(new MediaStream([track]));
    const node = new AudioWorkletNode(this.audioContext, "pcm-capture");
    node.port.onmessage = ({ data }) => this.onSpeechAudio?.(who, new Uint8Array(data));
    source.connect(node);
    node.connect(this.silentSink);
    this.captures.set(who, { source, node });
  }

  detachSpeechCapture(who) {
    const capture = this.captures.get(who);
    if (capture) {
      capture.node.port.onmessage = null;
      capture.source.disconnect();
      capture.node.disconnect();
      this.captures.delete(who);
    }
  }

  disableSpeechCapture() {
    for (const { source, node } of this.captures.values()) {
      node.port.onmessage = null;
      source.disconnect();
      node.disconnect();
    }
    this.captures.clear();
    this.onSpeechAudio = null;
  }

  // ---- Tela até 2K -------------------------------------------------------------------

  /** Compartilha a tela; stream permite testar com um vídeo sintético no lugar da captura. */
  async startScreenShare({ mode = "detail", stream = null } = {}) {
    if (this.screenTrack) {
      return;
    }
    const capture = stream ?? await navigator.mediaDevices.getDisplayMedia({
      video: {
        width: { max: SCREEN_MAX.width },
        height: { max: SCREEN_MAX.height },
        frameRate: { ideal: SCREEN_MAX.frameRate, max: SCREEN_MAX.frameRate },
      },
      audio: false,
      selfBrowserSurface: "exclude",
      surfaceSwitching: "include",
      systemAudio: "exclude",
    });
    const track = capture.getVideoTracks()[0];
    // Parar pelo próprio Windows também encerra o compartilhamento.
    track.onended = () => this.stopScreenShare();
    this.screenTrack = track;
    this.showLocalScreen();
    this.setScreenMode(mode);

    this.sendSignal({ screen: { streamId: this.screenStream.id } });
    if (this.screenTransceiver) {
      await this.screenTransceiver.sender.replaceTrack(track);
      this.screenTransceiver.direction = "sendonly";
    } else {
      // A ordem precisa estar pronta antes de o transceptor disparar a renegociação.
      const codecs = await this.screenCodecOrder();
      this.screenTransceiver = this.pc.addTransceiver(track, { direction: "sendonly", streams: [this.screenStream] });
      try {
        this.screenTransceiver.setCodecPreferences(codecs);
      } catch {
        // Sem preferência: o navegador usa a ordem padrão.
      }
    }
    this.events.onLocalScreen?.(true);
  }

  async stopScreenShare() {
    if (!this.screenTrack) {
      return;
    }
    const track = this.screenTrack;
    this.screenTrack = null;
    this.showLocalScreen();
    track.onended = null;
    track.stop();
    if (this.screenTransceiver && this.pc.signalingState !== "closed") {
      await this.screenTransceiver.sender.replaceTrack(null);
      this.screenTransceiver.direction = "inactive";
    }
    this.sendSignal({ screen: null });
    this.events.onLocalScreen?.(false);
  }

  showLocalScreen() {
    if (this.localScreenView) {
      this.localScreenView.muted = true;
      this.localScreenView.srcObject = this.screenTrack ? new MediaStream([this.screenTrack]) : null;
    }
  }

  setScreenMode(mode) {
    this.screenMode = mode === "motion" ? "motion" : "detail";
    if (this.screenTrack) {
      this.screenTrack.contentHint = this.screenMode;
    }
    this.tuneSenders();
  }

  /** Codificação por hardware primeiro; depois AV1 → VP9 → H.264; rtx, red e fec no fim. */
  async screenCodecOrder() {
    const codecs = RTCRtpReceiver.getCapabilities?.("video")?.codecs ?? [];
    const hardware = new Set();
    for (const contentType of SCREEN_CODECS) {
      try {
        const info = await navigator.mediaCapabilities.encodingInfo({
          type: "webrtc",
          video: { contentType, width: SCREEN_MAX.width, height: SCREEN_MAX.height, bitrate: SCREEN_BITRATE.detail, framerate: SCREEN_MAX.frameRate },
        });
        if (info.supported && info.powerEfficient) {
          hardware.add(contentType);
        }
      } catch {
        // Sem informação: fica na ordem padrão.
      }
    }
    const rank = (codec) => {
      const position = SCREEN_CODECS.indexOf(codec.mimeType);
      if (position === -1) {
        return SCREEN_CODECS.length * 2;
      }
      return (hardware.has(codec.mimeType) ? 0 : SCREEN_CODECS.length) + position;
    };
    return [...codecs].sort((a, b) => rank(a) - rank(b));
  }

  async tuneSenders() {
    for (const sender of this.pc.getSenders()) {
      if (!sender.track) {
        continue;
      }
      try {
        const parameters = sender.getParameters();
        if (!parameters.encodings || parameters.encodings.length === 0) {
          continue;
        }
        const encoding = parameters.encodings[0];
        if (sender.track.kind === "audio") {
          encoding.maxBitrate = AUDIO_MAX_BITRATE;
          encoding.priority = "high";
          encoding.networkPriority = "high";
        } else if (sender === this.screenTransceiver?.sender) {
          encoding.maxBitrate = SCREEN_BITRATE[this.screenMode];
          parameters.degradationPreference = this.screenMode === "detail" ? "maintain-resolution" : "maintain-framerate";
        } else {
          encoding.maxBitrate = QUALITIES[this.quality].bitrate;
          parameters.degradationPreference = "balanced";
        }
        await sender.setParameters(parameters);
      } catch {
        // Parâmetros podem ser recusados durante uma renegociação; tenta de novo na próxima.
      }
    }
  }

  // ---- Estatísticas -------------------------------------------------------------

  startStats() {
    if (this.statsTimer) {
      return;
    }
    this.statsTimer = setInterval(() => this.collectStats(), STATS_INTERVAL_MS);
  }

  stopStats() {
    clearInterval(this.statsTimer);
    this.statsTimer = null;
  }

  async collectStats() {
    try {
      const report = await this.pc.getStats();
      let pair = null;
      report.forEach((stat) => {
        if (stat.type === "transport" && stat.selectedCandidatePairId) {
          pair = report.get(stat.selectedCandidatePairId) ?? pair;
        }
      });
      if (!pair) {
        return;
      }
      const local = report.get(pair.localCandidateId);
      if (typeof pair.currentRoundTripTime === "number") {
        this.rttSamples.push(Math.round(pair.currentRoundTripTime * 1000));
        if (this.rttSamples.length > RTT_WINDOW) {
          this.rttSamples.shift();
        }
      }
      this.events.onStats?.(median(this.rttSamples), local?.candidateType ?? "desconhecido");
      await this.collectScreenStats();
    } catch {
      // Estatística é só informativa.
    }
  }

  /** Resolução, quadros por segundo e codec da tela enviada ou recebida. */
  async collectScreenStats() {
    const sending = Boolean(this.screenTrack && this.screenTransceiver);
    const source = sending ? this.screenTransceiver.sender : this.remoteScreenStreamId ? this.remoteScreenReceiver : null;
    if (!source) {
      return;
    }
    const report = await source.getStats();
    let rtp = null;
    report.forEach((stat) => {
      if (stat.type === (sending ? "outbound-rtp" : "inbound-rtp") && stat.kind === "video") {
        rtp = stat;
      }
    });
    if (!rtp?.frameWidth) {
      return;
    }
    const codec = report.get(rtp.codecId)?.mimeType?.replace("video/", "") ?? "";
    this.events.onScreenStats?.(sending, rtp.frameWidth, rtp.frameHeight, Math.round(rtp.framesPerSecond ?? 0), codec);
  }

  // ---- Mensagens do app e encerramento ----------------------------------------------

  sendApp(text) {
    if (typeof text === "string" && text.length <= MAX_APP_MESSAGE_LENGTH && this.app.readyState === "open") {
      this.app.send(text);
      return true;
    }
    return false;
  }

  close() {
    this.stopStats();
    this.disableSpeechCapture();
    this.audioContext?.close();
    this.microphone?.close();
    this.screenTrack?.stop();
    for (const track of this.localStream?.getTracks() ?? []) {
      track.stop();
    }
    this.pc.close();
  }
}

/** Mediana (-1 sem medidas). */
export function median(values) {
  if (values.length === 0) {
    return -1;
  }
  const sorted = [...values].sort((a, b) => a - b);
  return sorted[Math.floor(sorted.length / 2)];
}

/** Impressão digital SHA-256 do certificado DTLS, em maiúsculas. */
export function fingerprintOf(sdp) {
  const match = /^a=fingerprint:sha-256 ([0-9A-Fa-f:]+)\r?$/m.exec(sdp ?? "");
  return match ? match[1].toUpperCase() : null;
}

/** Pede ao outro lado para mandar Opus em alta qualidade: 128 kb/s, com FEC e sem DTX. */
export function tuneOpus(sdp) {
  const rtpmap = /^a=rtpmap:(\d+) opus\/48000\/2\r?$/m.exec(sdp);
  if (!rtpmap) {
    return sdp;
  }
  const payload = rtpmap[1];
  const wanted = { maxaveragebitrate: "128000", useinbandfec: "1", usedtx: "0", stereo: "0" };
  const fmtp = new RegExp(`^a=fmtp:${payload} (.*?)\\r?$`, "m");
  const existing = fmtp.exec(sdp);
  if (!existing) {
    const line = `a=fmtp:${payload} ${Object.entries(wanted).map(([k, v]) => `${k}=${v}`).join(";")}`;
    return sdp.replace(rtpmap[0], `${rtpmap[0]}\r\n${line}`);
  }
  const parameters = new Map(existing[1].split(";").filter(Boolean).map((p) => p.split("=")));
  for (const [key, value] of Object.entries(wanted)) {
    parameters.set(key, value);
  }
  const line = `a=fmtp:${payload} ${[...parameters].map(([k, v]) => `${k}=${v}`).join(";")}`;
  return sdp.replace(existing[0], line);
}

// ---- Ponte com o C# (uma chamada por vez) --------------------------------------------

let current = null;

export function create(dotnet, polite, relay) {
  current?.close();
  current = new CallPeer({
    onState: (state) => dotnet.invokeMethodAsync("OnConnectionState", state),
    onStats: (rtt, path) => dotnet.invokeMethodAsync("OnStats", rtt, path),
    onAppMessage: (text) => dotnet.invokeMethodAsync("OnAppMessage", text),
    onChannelOpen: () => dotnet.invokeMethodAsync("OnChannelOpen"),
    onError: (message) => dotnet.invokeMethodAsync("OnMediaError", message),
    onRemoteStream: () => {},
    onRemoteScreen: (active) => dotnet.invokeMethodAsync("OnRemoteScreen", active),
    onLocalScreen: (active) => dotnet.invokeMethodAsync("OnLocalScreen", active),
    onScreenStats: (sending, width, height, fps, codec) => dotnet.invokeMethodAsync("OnScreenStats", sending, width, height, fps, codec),
    onNoiseStatus: ({ mode, problem }) => dotnet.invokeMethodAsync("OnNoiseStatus", mode, problem),
  }, { polite, relay: relay ?? null });
}

/**
 * Diagnóstico da rede deste computador: IPv6 público, comportamento do NAT no IPv4 (um
 * endereço para todos os destinos = "direta"; um por destino = "restrita", típico do CGNAT
 * das operadoras) e, se configurada, se a retransmissão responde.
 */
export async function diagnoseNetwork(relay) {
  const gather = async (iceServers, iceTransportPolicy) => {
    const pc = new RTCPeerConnection({ iceServers, iceTransportPolicy });
    pc.createDataChannel("diagnostico");
    const found = [];
    pc.onicecandidate = ({ candidate }) => {
      if (candidate?.candidate) {
        found.push(candidate);
      }
    };
    await pc.setLocalDescription(await pc.createOffer());
    await new Promise((resolve) => {
      const timer = setTimeout(resolve, 6000);
      pc.onicegatheringstatechange = () => {
        if (pc.iceGatheringState === "complete") {
          clearTimeout(timer);
          resolve();
        }
      };
    });
    pc.close();
    return found;
  };
  const direct = await gather(ICE_SERVERS, "all");
  const reflexive = direct.filter((c) => c.type === "srflx" && c.protocol === "udp" && c.address);
  const ipv4 = new Set(reflexive.filter((c) => !c.address.includes(":")).map((c) => `${c.address}:${c.port}`));
  const result = {
    ipv6: reflexive.some((c) => c.address.includes(":")),
    ipv4: ipv4.size === 0 ? "sem" : ipv4.size === 1 ? "direta" : "restrita",
    relay: null,
  };
  if (relay) {
    result.relay = (await gather([relay], "relay")).some((c) => c.type === "relay");
  }
  return result;
}

export const createInvite = () => current.createInvite();
export const acceptInvite = (sdp) => current.acceptInvite(sdp);
export const acceptAnswer = (sdp) => current.acceptAnswer(sdp);
export const localSdp = () => current?.localSdp() ?? null;
export const sendApp = (text) => current?.sendApp(text) ?? false;

/** Envia e espera a mensagem sair (até 500 ms) — para o aviso de encerramento não se perder no fechamento. */
export async function sendAppFlushed(text) {
  if (!current?.sendApp(text)) {
    return false;
  }
  const channel = current.app;
  for (let waited = 0; channel.readyState === "open" && channel.bufferedAmount > 0 && waited < 500; waited += 20) {
    await new Promise((resolve) => setTimeout(resolve, 20));
  }
  // Os bytes saíram do buffer; um instante para o SCTP entregar antes de fechar.
  await new Promise((resolve) => setTimeout(resolve, 100));
  return true;
}
export const setTrackEnabled = (kind, enabled) => current?.setTrackEnabled(kind, enabled);
export const startScreenShare = (mode) => current.startScreenShare({ mode });

export async function enableSpeechCapture(dotnet) {
  await current.enableSpeechCapture((who, bytes) => dotnet.invokeMethodAsync("OnSpeechAudio", who, bytes));
}

export const disableSpeechCapture = () => current?.disableSpeechCapture();
export const stopScreenShare = () => current?.stopScreenShare();
export const setScreenMode = (mode) => current?.setScreenMode(mode);

/** Teste de ponta a ponta: compartilha um vídeo sintético no lugar da tela. */
export const shareStream = (stream, mode) => current.startScreenShare({ mode, stream });

export function bindRemoteScreen(video) {
  video.muted = true;
  video.srcObject = current.remoteScreen;
}

export function bindLocalScreen(video) {
  current.localScreenView = video;
  current.showLocalScreen();
}

let remoteElement = null;

/** Liga câmera e microfone; devolve o que ficou disponível ({ camera, microphone }). */
export async function startMedia(localVideo, remoteVideo, highFidelity) {
  remoteElement = remoteVideo;
  remoteVideo.srcObject = current.remoteStream;
  await applySpeaker(remoteVideo);
  const stream = await current.startLocalMedia({ highFidelity });
  showPreview(localVideo, stream);
  return { camera: current.hasLocalTrack("video"), microphone: current.hasLocalTrack("audio") };
}

export const setVideoQuality = (name) => current?.setVideoQuality(name);

/** Supressor de ruído desta chamada: as escolhas e o modo que está rodando de fato. */
export const noiseState = () => ({
  settings: current?.microphone?.settings ?? noiseSettings(),
  mode: current?.microphone?.status.mode ?? null,
  problem: current?.microphone?.status.problem ?? null,
});

export async function setNoise(value) {
  return current ? await current.setNoise(value) : saveNoiseSettings(value);
}

/** Liga (ou desliga, com null) o medidor do painel de ruído. */
export function bindNoiseMeter(fill, mark) {
  if (current) {
    current.noiseLevel = fill ? (level) => paintMeter(fill, mark, level) : null;
  }
}
export const currentVideoQuality = () => current?.quality ?? videoQuality();

/** Som da chamada (o que a outra pessoa fala). A tradução continua ouvindo mesmo mudo. */
export function setRemoteAudio(enabled) {
  if (remoteElement) {
    remoteElement.muted = !enabled;
  }
}

// ---- Janela flutuante -----------------------------------------------------------------
// Janela de vídeo do próprio Windows (picture-in-picture): sempre por cima, redimensionável e
// fácil de levar para outro monitor. Ela só mostra vídeo, então a imagem da outra pessoa (ou
// da tela dela) e a legenda são desenhadas juntas num quadro, e esse quadro vai para a janela.

const FLOATING_SIZE = { width: 1280, height: 720 };
const FLOATING_FPS = 30;
const FLOATING_MAIN_FONT = "500 44px 'Golos Text', 'Segoe UI', sans-serif";
const FLOATING_SECONDARY_FONT = "30px 'Golos Text', 'Segoe UI', sans-serif";

let floating = null;

export const canFloat = () => document.pictureInPictureEnabled === true;

export async function openFloating(dotnet, showScreen) {
  if (floating) {
    return;
  }
  const canvas = document.createElement("canvas");
  canvas.width = FLOATING_SIZE.width;
  canvas.height = FLOATING_SIZE.height;
  const source = document.createElement("video");
  source.muted = true;
  source.playsInline = true;
  const output = document.createElement("video");
  output.muted = true;
  output.playsInline = true;
  output.srcObject = canvas.captureStream(FLOATING_FPS);
  floating = { canvas, source, output, main: "", secondary: "", showScreen: null, timer: null, dotnet };
  setFloatingSource(showScreen);
  drawFloating();
  floating.timer = setInterval(drawFloating, 1000 / FLOATING_FPS);
  output.addEventListener("leavepictureinpicture", () => {
    const closing = floating;
    stopFloating();
    closing?.dotnet.invokeMethodAsync("OnFloatingClosed");
  });
  try {
    await output.play();
    await output.requestPictureInPicture();
  } catch (error) {
    stopFloating();
    throw error;
  }
}

function setFloatingSource(showScreen) {
  if (!floating || floating.showScreen === showScreen) {
    return;
  }
  floating.showScreen = showScreen;
  const stream = showScreen && current ? current.remoteScreen : current?.remoteStream;
  floating.source.srcObject = stream ?? null;
  floating.source.play().catch(() => {});
}

function drawFloating() {
  const state = floating;
  if (!state) {
    return;
  }
  const { canvas, source } = state;
  const context = canvas.getContext("2d");
  context.fillStyle = "#060a16";
  context.fillRect(0, 0, canvas.width, canvas.height);
  if (source.videoWidth > 0) {
    // Imagem inteira, sem corte (faixas pretas se a proporção for outra).
    const scale = Math.min(canvas.width / source.videoWidth, canvas.height / source.videoHeight);
    const width = source.videoWidth * scale;
    const height = source.videoHeight * scale;
    context.drawImage(source, (canvas.width - width) / 2, (canvas.height - height) / 2, width, height);
  }
  if (!state.main) {
    return;
  }
  // Cartão de vidro arredondado com a faixa azul da outra pessoa, como na janela principal.
  const margin = 48;
  const padding = 28;
  const maxWidth = canvas.width - margin * 2 - padding * 2 - 8;
  context.font = FLOATING_MAIN_FONT;
  const mainLines = wrapText(context, state.main, maxWidth, 3);
  context.font = FLOATING_SECONDARY_FONT;
  const secondaryLines = state.secondary ? wrapText(context, state.secondary, maxWidth, 2) : [];
  const boxHeight = padding * 2 + mainLines.length * 56 + secondaryLines.length * 38 - 10;
  const boxTop = canvas.height - boxHeight - 36;
  context.fillStyle = "rgba(13, 20, 40, .86)";
  context.beginPath();
  context.roundRect(margin, boxTop, canvas.width - margin * 2, boxHeight, 28);
  context.fill();
  context.strokeStyle = "rgba(255, 255, 255, .08)";
  context.lineWidth = 2;
  context.stroke();
  context.fillStyle = "#3d6fe0";
  context.beginPath();
  context.roundRect(margin, boxTop, 8, boxHeight, [28, 0, 0, 28]);
  context.fill();
  context.textAlign = "left";
  context.textBaseline = "top";
  let y = boxTop + padding;
  const x = margin + padding + 8;
  context.font = FLOATING_SECONDARY_FONT;
  context.fillStyle = "#a6afca";
  for (const line of secondaryLines) {
    context.fillText(line, x, y);
    y += 38;
  }
  context.font = FLOATING_MAIN_FONT;
  context.fillStyle = "#eef1fa";
  for (const line of mainLines) {
    context.fillText(line, x, y);
    y += 56;
  }
}

/** Quebra o texto em linhas; se passar do limite, fica o final (o que está sendo dito agora). */
function wrapText(context, text, maxWidth, maxLines) {
  const lines = [];
  let line = "";
  for (const word of text.split(/\s+/).filter(Boolean)) {
    const candidate = line ? `${line} ${word}` : word;
    if (context.measureText(candidate).width > maxWidth && line) {
      lines.push(line);
      line = word;
    } else {
      line = candidate;
    }
  }
  if (line) {
    lines.push(line);
  }
  return lines.length > maxLines ? ["…" + lines.slice(-maxLines)[0], ...lines.slice(-maxLines + 1)] : lines;
}

/** Legenda e fonte da janela flutuante. */
export function updateFloating(mainText, secondaryText, showScreen) {
  if (!floating) {
    return;
  }
  setFloatingSource(showScreen);
  floating.main = typeof mainText === "string" ? mainText : "";
  floating.secondary = typeof secondaryText === "string" ? secondaryText : "";
}

function stopFloating() {
  if (!floating) {
    return;
  }
  clearInterval(floating.timer);
  for (const track of floating.output.srcObject?.getTracks() ?? []) {
    track.stop();
  }
  floating.source.srcObject = null;
  floating = null;
}

export function closeFloating() {
  if (floating && document.pictureInPictureElement === floating.output) {
    document.exitPictureInPicture().catch(() => {});
  }
  stopFloating();
}

export function hangup() {
  closeFloating();
  current?.close();
  current = null;
  remoteElement = null;
}
