"""Motor auxiliar do DinoTranslator: faster-whisper (reconhecimento) e CTranslate2 (NLLB e OPUS-MT).

Roda como processo filho do app e conversa só pela entrada e saída padrão: uma mensagem JSON por
linha. Não abre portas, não acessa a internet (os modelos já vêm baixados e conferidos pelo app) e
termina quando o app fecha a entrada. Reconhecimento e tradução têm filas próprias, para a placa
de vídeo e o processador trabalharem ao mesmo tempo.
"""

import base64
import glob
import json
import os
import queue
import re
import sys
import threading
import time

os.environ["HF_HUB_OFFLINE"] = "1"
os.environ["HF_HUB_DISABLE_TELEMETRY"] = "1"

# Bibliotecas da NVIDIA (cuBLAS, cuDNN): na pasta "cuda12" ao lado do Python, ou nos pacotes pip.
_runtime = os.path.dirname(sys.executable)
for folder in [os.path.join(os.path.dirname(_runtime), "cuda12")] + glob.glob(
    os.path.join(sys.prefix, "Lib", "site-packages", "nvidia", "*", "bin")
):
    if os.path.isdir(folder):
        os.add_dll_directory(folder)
        os.environ["PATH"] = folder + os.pathsep + os.environ.get("PATH", "")

import ctranslate2  # noqa: E402
import numpy as np  # noqa: E402
import sentencepiece  # noqa: E402

MAX_AUDIO_SECONDS = 30
MAX_TEXT = 2000

NLLB_CODES = {"pt": "por_Latn", "en": "eng_Latn", "es": "spa_Latn", "ru": "rus_Cyrl", "uk": "ukr_Cyrl"}
# Etiqueta do idioma de destino nos modelos OPUS-MT de vários destinos; o português do Brasil
# ("pob") tem a sua, quando o modelo conhece.
OPUS_TAGS = {"pt": ["pob", "por"], "en": ["eng"], "es": ["spa"], "ru": ["rus"], "uk": ["ukr"]}

# Fim de frase seguido de espaço: o NLLB e o OPUS-MT foram treinados frase a frase e, com duas
# frases juntas, às vezes deixam a segunda de fora.
SENTENCE_END = re.compile(r"(?<=[.!?…])\s+")


def sentences(text):
    parts = [part for part in SENTENCE_END.split(text.strip()) if part]
    return parts or [text]

_write_lock = threading.Lock()
_models = {}
_models_lock = threading.Lock()


def send(message):
    line = json.dumps(message, ensure_ascii=False)
    with _write_lock:
        sys.stdout.write(line + "\n")
        sys.stdout.flush()


def cuda_available():
    try:
        return ctranslate2.get_cuda_device_count() > 0
    except Exception:  # noqa: BLE001 - sem CUDA, sem placa
        return False


class Recognizer:
    def __init__(self, path, device, compute):
        from faster_whisper import WhisperModel

        self.model = WhisperModel(path, device=device, compute_type=compute, cpu_threads=max(2, (os.cpu_count() or 4) // 2))

    def run(self, request):
        pcm = base64.b64decode(request["audio"])
        samples = np.frombuffer(pcm, dtype="<i2").astype(np.float32) / 32768.0
        if len(samples) > 16000 * MAX_AUDIO_SECONDS:
            raise ValueError("áudio longo demais")
        prompt = request.get("prompt") or None
        segments, _ = self.model.transcribe(
            samples,
            language=request["language"],
            beam_size=int(request.get("beam", 1)),
            condition_on_previous_text=False,
            without_timestamps=True,
            vad_filter=False,
            initial_prompt=prompt,
        )
        return {"text": "".join(segment.text for segment in segments).strip()}


class Translator:
    """NLLB (um modelo para todos os pares) ou OPUS-MT (um modelo por grupo de idiomas)."""

    def __init__(self, path, kind, device, compute):
        self.kind = kind
        self.translator = ctranslate2.Translator(path, device=device, compute_type=compute, inter_threads=1, intra_threads=max(2, (os.cpu_count() or 4) // 2))
        if kind == "nllb":
            self.source_spm = self.target_spm = sentencepiece.SentencePieceProcessor(model_file=os.path.join(path, "sentencepiece.bpe.model"))
            self.tags = set()
        else:
            self.source_spm = sentencepiece.SentencePieceProcessor(model_file=os.path.join(path, "source.spm"))
            self.target_spm = sentencepiece.SentencePieceProcessor(model_file=os.path.join(path, "target.spm"))
            vocabulary = next(
                (os.path.join(path, f) for f in ("shared_vocabulary.json", "target_vocabulary.json") if os.path.exists(os.path.join(path, f))),
                None,
            )
            tags = []
            if vocabulary:
                with open(vocabulary, encoding="utf-8") as handle:
                    tags = [t for t in json.load(handle) if t.startswith(">>") and t.endswith("<<")]
            self.tags = set(tags)

    def run(self, request):
        text = request["text"]
        if not text or len(text) > MAX_TEXT:
            raise ValueError("texto vazio ou longo demais")
        source, target = request["source"][:2], request["target"][:2]
        beam = int(request.get("beam", 1))
        batch = []
        for sentence in sentences(text):
            pieces = self.source_spm.encode(sentence, out_type=str)
            if self.kind == "nllb":
                batch.append([NLLB_CODES[source]] + pieces + ["</s>"])
            else:
                tag = next((f">>{code}<<" for code in OPUS_TAGS[target] if f">>{code}<<" in self.tags), None)
                batch.append(([tag] if tag else []) + pieces + ["</s>"])
        prefix = [[NLLB_CODES[target]]] * len(batch) if self.kind == "nllb" else None
        results = self.translator.translate_batch(
            batch,
            target_prefix=prefix,
            beam_size=beam,
            max_decoding_length=min(256, max(len(tokens) for tokens in batch) * 3 + 10),
        )
        translated = []
        for result in results:
            output = result.hypotheses[0]
            if self.kind == "nllb" and output and output[0] == NLLB_CODES[target]:
                output = output[1:]
            translated.append(self.target_spm.decode(output).strip())
        return {"text": " ".join(part for part in translated if part)}


def load(request):
    key = request["key"]
    with _models_lock:
        if key in _models:
            return {"loaded": key}
    start = time.perf_counter()
    if request["kind"] == "whisper":
        model = Recognizer(request["path"], request["device"], request["compute"])
    else:
        model = Translator(request["path"], request["kind"], request["device"], request["compute"])
    with _models_lock:
        _models[key] = model
    return {"loaded": key, "ms": round((time.perf_counter() - start) * 1000)}


def unload(request):
    with _models_lock:
        _models.pop(request["key"], None)
    return {}


def run(request):
    with _models_lock:
        model = _models.get(request["key"])
    if model is None:
        raise KeyError("modelo não carregado: " + request["key"])
    start = time.perf_counter()
    answer = model.run(request)
    answer["ms"] = round((time.perf_counter() - start) * 1000)
    return answer


HANDLERS = {
    "hello": lambda r: {"cuda": cuda_available(), "ctranslate2": ctranslate2.__version__},
    "load": load,
    "unload": unload,
    "run": run,
}


def worker(jobs):
    while True:
        request = jobs.get()
        if request is None:
            return
        try:
            answer = HANDLERS[request["op"]](request)
            answer["id"] = request["id"]
            answer["ok"] = True
        except Exception as error:  # noqa: BLE001 - o erro volta para o app, sem derrubar o motor
            answer = {"id": request.get("id"), "ok": False, "error": f"{type(error).__name__}: {error}"}
        send(answer)


def main():
    sys.stdout.reconfigure(encoding="utf-8", newline="\n")
    sys.stdin.reconfigure(encoding="utf-8")
    # Uma fila para reconhecer (placa de vídeo), outra para traduzir e uma para o resto.
    queues = {name: queue.Queue() for name in ("asr", "mt", "control")}
    threads = [threading.Thread(target=worker, args=(q,), daemon=True) for q in queues.values()]
    for thread in threads:
        thread.start()
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            request = json.loads(line)
        except json.JSONDecodeError:
            continue
        if not isinstance(request, dict) or request.get("op") not in HANDLERS:
            continue
        lane = request.get("lane")
        queues[lane if lane in ("asr", "mt") else "control"].put(request)
    for q in queues.values():
        q.put(None)


if __name__ == "__main__":
    main()
