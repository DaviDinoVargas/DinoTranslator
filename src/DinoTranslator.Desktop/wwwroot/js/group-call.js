// Chamada em grupo da versão Plus, pelo servidor de mídia (LiveKit, SFU) do seu servidor.
// O microfone passa pela mesma cadeia do supressor de ruído da chamada por código (noise.js).
// As legendas viajam pelo canal de dados da sala (tópico "legendas"): cada app reconhece a
// própria fala e manda o texto original com as traduções para os idiomas de quem está na sala.
import { Room, RoomEvent, Track, createLocalVideoTrack, createLocalScreenTracks } from "./vendor/livekit/livekit-client.esm.js";
import { MicrophoneChain, browserProcessing, noiseSettings, paintMeter, saveNoiseSettings } from "./noise.js";
import { preference, speechContext } from "./call.js";

const CAPTION_TOPIC = "legendas";
const MAX_CAPTION_BYTES = 8 * 1024;

let call = null;

/** Idioma e usuário que o servidor pôs na permissão de cada pessoa. */
function metadataOf(participant) {
  try {
    const data = JSON.parse(participant.metadata || "{}");
    return { lang: typeof data.lang === "string" ? data.lang : "", user: typeof data.user === "string" ? data.user : "" };
  } catch {
    return { lang: "", user: "" };
  }
}

function participantsOf(room) {
  const all = [room.localParticipant, ...room.remoteParticipants.values()];
  return all.map((p) => ({
    id: p.identity,
    name: p.name || p.identity,
    language: metadataOf(p).lang,
    local: p === room.localParticipant,
    speaking: p.isSpeaking,
    microphone: p.isMicrophoneEnabled,
    camera: p.isCameraEnabled,
    screen: p.isScreenShareEnabled,
    screenAudio: Boolean(p.getTrackPublication(Track.Source.ScreenShareAudio)),
  }));
}

/** Entra na sala; grid é o elemento onde ficam os quadros (pode mudar depois com bindGrid). */
export async function join(dotnet, url, token, grid) {
  await leave();
  const room = new Room({ adaptiveStream: true, dynacast: true, disconnectOnPageLeave: true });
  const audio = document.createElement("div");
  audio.hidden = true;
  document.body.appendChild(audio);
  call = { room, dotnet, grid, audio, microphone: null, rawMicrophone: null, capture: null, deafened: false, tiles: new Map(), screenTracks: [] };
  const report = () => dotnet.invokeMethodAsync("OnParticipants", participantsOf(room)).catch(() => {});

  room
    .on(RoomEvent.TrackSubscribed, (track, publication, participant) => {
      if (track.kind === Track.Kind.Audio) {
        const element = track.attach();
        element.muted = call?.deafened ?? false;
        audio.appendChild(element);
        applySpeaker(element);
      } else {
        attachVideo(participant, track, publication.source);
      }
      report();
    })
    .on(RoomEvent.TrackUnsubscribed, (track, publication, participant) => {
      track.detach().forEach((element) => element.remove());
      if (track.kind === Track.Kind.Video) {
        detachVideo(participant.identity, publication.source);
      }
      report();
    })
    .on(RoomEvent.LocalTrackPublished, (publication) => {
      if (publication.track?.kind === Track.Kind.Video) {
        attachVideo(room.localParticipant, publication.track, publication.source);
      }
      report();
    })
    .on(RoomEvent.LocalTrackUnpublished, (publication) => {
      detachVideo(room.localParticipant.identity, publication.source);
      report();
    })
    .on(RoomEvent.ParticipantConnected, report)
    .on(RoomEvent.ParticipantDisconnected, (participant) => {
      for (const key of [...call.tiles.keys()].filter((k) => k.startsWith(participant.identity + ":"))) {
        call.tiles.get(key)?.remove();
        call.tiles.delete(key);
      }
      report();
    })
    .on(RoomEvent.ParticipantMetadataChanged, report)
    .on(RoomEvent.TrackMuted, report)
    .on(RoomEvent.TrackUnmuted, report)
    .on(RoomEvent.ActiveSpeakersChanged, (speakers) => {
      const ids = new Set(speakers.map((s) => s.identity));
      for (const [key, tile] of call.tiles) {
        tile.classList.toggle("tile--speaking", ids.has(key.split(":")[0]));
      }
      report();
    })
    .on(RoomEvent.DataReceived, (payload, participant, _kind, topic) => {
      if (topic !== CAPTION_TOPIC || !participant || payload.byteLength > MAX_CAPTION_BYTES) {
        return;
      }
      const text = new TextDecoder().decode(payload);
      dotnet.invokeMethodAsync("OnCaption", participant.identity, participant.name || "", text).catch(() => {});
    })
    .on(RoomEvent.Disconnected, () => dotnet.invokeMethodAsync("OnDisconnected").catch(() => {}));

  await room.connect(url, token);
  await startMicrophone();
  report();
  return participantsOf(room);
}

async function applySpeaker(element) {
  const speaker = preference("speaker");
  if (speaker && typeof element.setSinkId === "function") {
    await element.setSinkId(speaker).catch(() => {});
  }
}

/** Microfone pela cadeia do supressor (o mesmo da chamada por código) e publicado na sala. */
async function startMicrophone() {
  const settings = noiseSettings();
  const microphone = preference("microphone");
  const stream = await navigator.mediaDevices.getUserMedia({
    audio: { ...(microphone ? { deviceId: { ideal: microphone } } : {}), ...browserProcessing(settings), sampleRate: 48000, channelCount: 1 },
  });
  call.rawMicrophone = stream.getAudioTracks()[0];
  let published = null;
  call.microphone = new MicrophoneChain({
    onTrack: async (track) => {
      if (published?.track) {
        // Faixa nova da cadeia (outro modo de ruído): troca sem republicar.
        await published.track.replaceTrack(track, true);
      } else {
        published = await call.room.localParticipant.publishTrack(track, { source: Track.Source.Microphone, name: "microfone" });
        call.publishedMicrophone = published;
      }
      reattachCapture(track);
    },
    onStatus: ({ mode, problem }) => call?.dotnet.invokeMethodAsync("OnNoiseStatus", mode, problem).catch(() => {}),
    onLevel: (level) => call?.noiseLevel?.(level),
  });
  await call.microphone.apply(call.rawMicrophone, settings);
}

function attachVideo(participant, track, source) {
  if (!call) {
    return;
  }
  const key = `${participant.identity}:${source}`;
  let tile = call.tiles.get(key);
  if (!tile) {
    tile = document.createElement("figure");
    tile.className = source === Track.Source.ScreenShare ? "tile tile--screen" : "tile";
    const caption = document.createElement("figcaption");
    caption.textContent = (participant.name || participant.identity) + (source === Track.Source.ScreenShare ? " · tela" : "");
    tile.appendChild(caption);
    call.tiles.set(key, tile);
  }
  tile.querySelector("video")?.remove();
  const video = track.attach();
  video.muted = true;
  video.playsInline = true;
  tile.prepend(video);
  call.grid?.appendChild(tile);
}

function detachVideo(identity, source) {
  const key = `${identity}:${source}`;
  call?.tiles.get(key)?.remove();
  call?.tiles.delete(key);
}

/** A tela da chamada foi aberta de novo (o app trocou de canal e voltou): repõe os quadros. */
export function bindGrid(grid) {
  if (!call) {
    return;
  }
  call.grid = grid;
  for (const tile of call.tiles.values()) {
    grid?.appendChild(tile);
  }
}

export async function setMicrophone(enabled) {
  if (call?.rawMicrophone) {
    call.rawMicrophone.enabled = enabled;
    await call.publishedMicrophone?.[enabled ? "unmute" : "mute"]?.();
  }
}

/** Sem som da sala (e sem microfone, como no Discord). */
export async function setDeafened(deafened) {
  if (!call) {
    return;
  }
  call.deafened = deafened;
  for (const element of call.audio.querySelectorAll("audio")) {
    element.muted = deafened;
  }
}

export async function setCamera(enabled) {
  if (!call) {
    return false;
  }
  const local = call.room.localParticipant;
  if (enabled) {
    const camera = preference("camera");
    const track = await createLocalVideoTrack({ ...(camera ? { deviceId: camera } : {}), resolution: { width: 1280, height: 720, frameRate: 30 } });
    await local.publishTrack(track, { source: Track.Source.Camera, simulcast: true });
  } else {
    const publication = local.getTrackPublication(Track.Source.Camera);
    if (publication?.track) {
      await local.unpublishTrack(publication.track, true);
    }
  }
  return local.isCameraEnabled;
}

export async function setScreen(enabled) {
  if (!call) {
    return false;
  }
  const local = call.room.localParticipant;
  if (enabled) {
    // Com o som do que está na tela, sem o som do próprio app (evita eco) e sem processamento de voz.
    call.screenTracks = await createLocalScreenTracks({
      audio: { echoCancellation: false, noiseSuppression: false, autoGainControl: false, channelCount: 2 },
      systemAudio: "include",
      suppressLocalAudioPlayback: false,
      resolution: { width: 2560, height: 1440, frameRate: 30 },
      contentHint: "detail",
    });
    for (const track of call.screenTracks) {
      if (track.kind === Track.Kind.Video) {
        track.mediaStreamTrack.addEventListener("ended", () => setScreen(false));
      }
      // Cada faixa já vem marcada como tela ou som da tela.
      await local.publishTrack(track, track.kind === Track.Kind.Video ? { simulcast: false } : { dtx: false, red: true });
    }
  } else {
    for (const track of call.screenTracks) {
      await local.unpublishTrack(track, true);
    }
    call.screenTracks = [];
  }
  call.dotnet.invokeMethodAsync("OnParticipants", participantsOf(call.room)).catch(() => {});
  return local.isScreenShareEnabled;
}

// ---- Legendas --------------------------------------------------------------------

/** Liga o envio da própria fala (16 kHz) para o C# reconhecer e traduzir. */
export async function enableCaptions() {
  if (!call || call.capture) {
    return;
  }
  const context = speechContext();
  await context.audioWorklet.addModule(new URL("./pcm-capture.js", import.meta.url));
  const silent = context.createGain();
  silent.gain.value = 0;
  silent.connect(context.destination);
  await context.resume();
  call.capture = { context, silent, source: null, node: null };
  reattachCapture(call.microphone?.track ?? call.rawMicrophone);
}

function reattachCapture(track) {
  const capture = call?.capture;
  if (!capture || !track) {
    return;
  }
  capture.source?.disconnect();
  capture.node?.disconnect();
  capture.source = capture.context.createMediaStreamSource(new MediaStream([track]));
  capture.node = new AudioWorkletNode(capture.context, "pcm-capture");
  capture.node.port.onmessage = ({ data }) => call?.dotnet.invokeMethodAsync("OnSpeechAudio", new Uint8Array(data)).catch(() => {});
  capture.source.connect(capture.node);
  capture.node.connect(capture.silent);
}

/** Manda uma legenda (JSON pronto, feito no C#) para todos da sala. */
export async function sendCaption(json) {
  if (!call || typeof json !== "string" || json.length > MAX_CAPTION_BYTES) {
    return;
  }
  await call.room.localParticipant.publishData(new TextEncoder().encode(json), { reliable: true, topic: CAPTION_TOPIC });
}

// ---- Supressão de ruído ------------------------------------------------------------

/** Volume (0–100) e mudo da voz ("person") ou do som da tela ("screen") de uma pessoa da sala. */
export function setParticipantVolume(identity, kind, percent, muted) {
  const participant = call?.room.remoteParticipants.get(identity);
  if (!participant) {
    return;
  }
  const volume = muted ? 0 : Math.min(1, Math.max(0, Number(percent) / 100 || 0));
  participant.setVolume(volume, kind === "screen" ? Track.Source.ScreenShareAudio : Track.Source.Microphone);
}

export function noiseState() {
  return {
    settings: call?.microphone?.settings ?? noiseSettings(),
    mode: call?.microphone?.status.mode ?? null,
    problem: call?.microphone?.status.problem ?? null,
  };
}

export async function setNoise(value) {
  const before = call?.microphone?.settings ?? noiseSettings();
  const settings = saveNoiseSettings(value);
  if (!call?.microphone || !call.rawMicrophone) {
    return settings;
  }
  const newRaw = before.echoCancellation !== settings.echoCancellation || before.autoGain !== settings.autoGain
    || (before.mode === "navegador") !== (settings.mode === "navegador");
  if (newRaw) {
    const old = call.rawMicrophone;
    const microphone = preference("microphone");
    const stream = await navigator.mediaDevices.getUserMedia({
      audio: { ...(microphone ? { deviceId: { ideal: microphone } } : {}), ...browserProcessing(settings), sampleRate: 48000, channelCount: 1 },
    });
    call.rawMicrophone = stream.getAudioTracks()[0];
    call.rawMicrophone.enabled = old.enabled;
    old.stop();
  }
  if (newRaw || before.mode !== settings.mode || call.microphone.status.mode !== settings.mode
      || (settings.mode === "deepfilter" && before.intensity !== settings.intensity)) {
    await call.microphone.apply(call.rawMicrophone, settings);
  } else {
    call.microphone.setSensitivity(settings);
  }
  return settings;
}

export function bindNoiseMeter(fill, mark) {
  if (call) {
    call.noiseLevel = fill ? (level) => paintMeter(fill, mark, level) : null;
  }
}

export async function leave() {
  if (!call) {
    return;
  }
  const current = call;
  call = null;
  current.capture?.source?.disconnect();
  current.capture?.node?.disconnect();
  await current.capture?.context.close().catch(() => {});
  current.microphone?.close();
  current.rawMicrophone?.stop();
  for (const track of current.screenTracks) {
    track.stop();
  }
  for (const tile of current.tiles.values()) {
    tile.remove();
  }
  await current.room.disconnect().catch(() => {});
  current.audio.remove();
}
