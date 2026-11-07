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

export class CallPeer {
  /**
   * @param {object} events callbacks: onState, onStats, onAppMessage, onChannelOpen, onRemoteStream, onError
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
    this.pc.ontrack = ({ track, receiver }) => {
      this.remoteStream.addTrack(track);
      track.onended = () => this.remoteStream.removeTrack(track);
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
    }
  }

  setTrackEnabled(kind, enabled) {
    for (const track of this.localStream?.getTracks() ?? []) {
      if (track.kind === kind) {
        track.enabled = enabled;
      }
    }
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
    } catch {
      // Estatística é só informativa.
    }
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
  }, { polite });
}

export const createInvite = () => current.createInvite();
export const acceptInvite = (sdp) => current.acceptInvite(sdp);
export const acceptAnswer = (sdp) => current.acceptAnswer(sdp);
export const localSdp = () => current?.localSdp() ?? null;
export const sendApp = (text) => current?.sendApp(text) ?? false;
export const setTrackEnabled = (kind, enabled) => current?.setTrackEnabled(kind, enabled);

export async function startMedia(localVideo, remoteVideo, highFidelity) {
  remoteVideo.srcObject = current.remoteStream;
  const stream = await current.startLocalMedia({ highFidelity });
  localVideo.srcObject = stream;
}

export function hangup() {
  current?.close();
  current = null;
}
