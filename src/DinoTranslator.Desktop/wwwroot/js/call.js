// Camada de mídia da chamada: WebRTC do WebView2.
// O código de convite/resposta só abre os canais de dados; câmera, áudio e tela são
// negociados depois pelo canal "sinal", por dentro da conexão já cifrada (DTLS).
// A lógica do app (códigos, perfil, mensagens) fica em C#.

const ICE_SERVERS = [
  { urls: ["stun:stun.l.google.com:19302", "stun:stun1.l.google.com:19302"] },
  { urls: "stun:stun.cloudflare.com:3478" },
];
const GATHER_TIMEOUT_MS = 4000;
const STATS_INTERVAL_MS = 1000;
const MAX_SIGNAL_LENGTH = 64 * 1024;
const MAX_APP_MESSAGE_LENGTH = 16 * 1024;
const AUDIO_MAX_BITRATE = 128_000;
const VIDEO_MAX_BITRATE = 2_500_000;
const SCREEN_MAX = { width: 2560, height: 1440, frameRate: 30 };
// Tela "nítida" guarda a resolução (texto, código); "fluida" guarda os quadros por segundo (vídeo).
const SCREEN_BITRATE = { detail: 6_000_000, motion: 8_000_000 };
const SCREEN_CODECS = ["video/AV1", "video/VP9", "video/H264"];
const MAX_STREAM_ID_LENGTH = 64;

export class CallPeer {
  /**
   * @param {object} events callbacks: onState, onStats, onAppMessage, onChannelOpen, onRemoteStream,
   *   onRemoteScreen, onLocalScreen, onScreenStats, onError
   * @param {{polite: boolean}} options o lado "educado" cede quando os dois renegociam ao mesmo tempo
   */
  constructor(events, { polite }) {
    this.events = events;
    this.polite = polite;
    this.makingOffer = false;
    this.ignoreOffer = false;
    this.pendingNegotiation = false;
    this.remoteFingerprint = null;
    this.localStream = null;
    this.remoteStream = new MediaStream();
    this.statsTimer = null;

    // Tela compartilhada: um transceptor só para ela, reaproveitado a cada compartilhamento,
    // e um fluxo de id fixo que o outro lado usa para separar a tela da câmera.
    this.screenTransceiver = null;
    this.screenStream = new MediaStream();
    this.screenTrack = null;
    this.screenMode = "detail";
    this.remoteScreenStreamId = null;
    this.remoteScreen = new MediaStream();
    this.remoteScreenReceiver = null;

    // Captura da fala para a tradução (só no computador que traduz).
    this.audioContext = null;
    this.silentSink = null;
    this.captures = new Map();
    this.onSpeechAudio = null;

    this.pc = new RTCPeerConnection({ iceServers: ICE_SERVERS, bundlePolicy: "max-bundle", rtcpMuxPolicy: "require" });
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
    const processing = !highFidelity;
    const stream = await navigator.mediaDevices.getUserMedia({
      audio: {
        echoCancellation: processing,
        noiseSuppression: processing,
        autoGainControl: processing,
        sampleRate: 48000,
        channelCount: 1,
      },
      video: {
        width: { ideal: 1920 },
        height: { ideal: 1080 },
        frameRate: { ideal: 30, max: 30 },
      },
    });
    this.addStream(stream);
    return stream;
  }

  addStream(stream) {
    this.localStream = stream;
    for (const track of stream.getTracks()) {
      if (track.kind === "video") {
        track.contentHint = "motion";
      }
      this.pc.addTrack(track, stream);
      if (track.kind === "audio") {
        this.attachSpeechCapture("local", track);
      }
    }
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
      this.audioContext = new AudioContext();
      await this.audioContext.audioWorklet.addModule(new URL("./pcm-capture.js", import.meta.url));
      // Mantém o grafo de áudio ativo sem tocar nada de novo: saída com volume zero.
      this.silentSink = this.audioContext.createGain();
      this.silentSink.gain.value = 0;
      this.silentSink.connect(this.audioContext.destination);
    }
    await this.audioContext.resume();
    this.attachSpeechCapture("local", this.localStream?.getAudioTracks()[0]);
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
    track.onended = null;
    track.stop();
    if (this.screenTransceiver && this.pc.signalingState !== "closed") {
      await this.screenTransceiver.sender.replaceTrack(null);
      this.screenTransceiver.direction = "inactive";
    }
    this.sendSignal({ screen: null });
    this.events.onLocalScreen?.(false);
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
          encoding.maxBitrate = VIDEO_MAX_BITRATE;
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
      const rtt = typeof pair.currentRoundTripTime === "number" ? Math.round(pair.currentRoundTripTime * 1000) : -1;
      this.events.onStats?.(rtt, local?.candidateType ?? "desconhecido");
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
    this.screenTrack?.stop();
    for (const track of this.localStream?.getTracks() ?? []) {
      track.stop();
    }
    this.pc.close();
  }
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

export function create(dotnet, polite) {
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
  }, { polite });
}

export const createInvite = () => current.createInvite();
export const acceptInvite = (sdp) => current.acceptInvite(sdp);
export const acceptAnswer = (sdp) => current.acceptAnswer(sdp);
export const localSdp = () => current?.localSdp() ?? null;
export const sendApp = (text) => current?.sendApp(text) ?? false;
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
  video.srcObject = current.remoteScreen;
}

export async function startMedia(localVideo, remoteVideo, highFidelity) {
  remoteVideo.srcObject = current.remoteStream;
  const stream = await current.startLocalMedia({ highFidelity });
  localVideo.srcObject = stream;
}

export function hangup() {
  current?.close();
  current = null;
}
