// Comparativo dos motores numa chamada de verdade: o Dino "fala" um arquivo de áudio (no lugar
// do microfone), a Anna fica em silêncio, e o painel Motores do Dino troca as combinações. Para
// cada uma, espera algumas frases e anota as médias de tempo e as legendas.
// Chamado por motores.ps1: node motores.mjs <portaA> <portaB>
const [portA, portB] = process.argv.slice(2).map(Number);

async function connect(port) {
  for (let attempt = 0; attempt < 90; attempt++) {
    try {
      const targets = await (await fetch(`http://127.0.0.1:${port}/json`)).json();
      const page = targets.find((t) => t.type === "page" && t.url.includes("0.0.0."));
      if (page) {
        const socket = new WebSocket(page.webSocketDebuggerUrl);
        await new Promise((resolve, reject) => { socket.onopen = resolve; socket.onerror = reject; });
        let id = 0;
        const pending = new Map();
        socket.onmessage = ({ data }) => {
          const message = JSON.parse(data);
          if (message.id && pending.has(message.id)) {
            pending.get(message.id)(message);
            pending.delete(message.id);
          }
        };
        const send = (method, params) => new Promise((resolve) => {
          const messageId = ++id;
          pending.set(messageId, resolve);
          socket.send(JSON.stringify({ id: messageId, method, params }));
        });
        const evaluate = async (expression) => {
          const m = await send("Runtime.evaluate", { expression, awaitPromise: true, returnByValue: true, userGesture: true });
          if (m.result?.exceptionDetails) {
            throw new Error(JSON.stringify(m.result.exceptionDetails));
          }
          return m.result?.result?.value;
        };
        const screenshot = async (path) => {
          const m = await send("Page.captureScreenshot", { format: "png" });
          const { writeFileSync } = await import("node:fs");
          writeFileSync(path, Buffer.from(m.result.data, "base64"));
        };
        return { evaluate, screenshot, close: () => socket.close() };
      }
    } catch {
      // App ainda subindo.
    }
    await new Promise((r) => setTimeout(r, 500));
  }
  throw new Error(`sem página na porta ${port}`);
}

const waitFor = async (app, expression, what, ms = 30000) => {
  const start = Date.now();
  while (Date.now() - start < ms) {
    const value = await app.evaluate(expression).catch(() => null);
    if (value) {
      return value;
    }
    await new Promise((r) => setTimeout(r, 300));
  }
  throw new Error("tempo esgotado: " + what);
};

const click = (label) => `(() => { const b = [...document.querySelectorAll('button')].find(b => b.textContent.trim().startsWith(${JSON.stringify(label)})); if (!b || b.disabled) return false; b.click(); return true; })()`;
const fill = (aria, value) => `(() => { const t = document.querySelector('textarea[aria-label="${aria}"]'); t.value = ${JSON.stringify(value)}; t.dispatchEvent(new Event('input', { bubbles: true })); return true; })()`;
const read = (selector) => `document.querySelector(${JSON.stringify(selector)})?.value || null`;
const choose = (aria, value) => `(() => { const s = document.querySelector('.engines select[aria-label="${aria}"]'); if (!s || s.disabled) return false; const o = [...s.options].find(o => o.value === ${JSON.stringify(value)}); if (!o || o.disabled) return 'indisponivel'; s.value = o.value; s.dispatchEvent(new Event('change', { bubbles: true })); return true; })()`;

const combinations = [
  { nome: "original", detector: "Volume", pausa: "800", reconhecimento: "WhisperTurbo", traducao: "Nllb600", final: "cuidadosa" },
  { nome: "silero-500", detector: "Silero", pausa: "500", reconhecimento: "WhisperTurbo", traducao: "Nllb600", final: "cuidadosa" },
  { nome: "marian-placa", detector: "Silero", pausa: "500", reconhecimento: "WhisperTurbo", traducao: "OpusGpu", final: "cuidadosa" },
  { nome: "nllb13-placa", detector: "Silero", pausa: "500", reconhecimento: "WhisperTurbo", traducao: "Nllb13Gpu", final: "cuidadosa" },
  { nome: "faster-whisper", detector: "Silero", pausa: "500", reconhecimento: "FasterWhisperGpu", traducao: "OpusGpu", final: "cuidadosa" },
];

const result = { combinacoes: [] };
const a = await connect(portA);
const b = await connect(portB);
try {
  await waitFor(a, click("Nova chamada"), "Nova chamada (A)", 60000);
  const invite = await waitFor(a, `(${read('textarea[aria-label="Código de convite"]')} || '').startsWith('DT1.') && ${read('textarea[aria-label="Código de convite"]')}`, "convite");
  await waitFor(b, click("Entrar com código"), "Entrar com código (B)", 60000);
  await waitFor(b, `!!document.querySelector('textarea[aria-label="Código de convite"]')`, "campo do convite (B)");
  await b.evaluate(fill("Código de convite", invite));
  await waitFor(b, click("Entrar na chamada"), "Entrar na chamada");
  await waitFor(a, `document.querySelector('.call__digits')?.textContent`, "chamada conectada", 60000);
  result.traducao = await waitFor(a, `document.querySelector('.conversation__where')?.textContent.trim()`, "onde traduz");

  await waitFor(a, click("Motores"), "botão Motores");
  await waitFor(a, `!!document.querySelector('.engines select')`, "painel de motores", 60000);
  result.opcoes = await a.evaluate(`[...document.querySelectorAll('.engines select')].map(s => s.getAttribute('aria-label') + ': ' + [...s.options].filter(o => !o.disabled).map(o => o.value).join(', '))`);

  for (const combo of combinations) {
    const steps = [["Detector de fala", combo.detector], ["Pausa que fecha a frase", combo.pausa], ["Reconhecimento de fala", combo.reconhecimento], ["Tradução", combo.traducao], ["Versão final", combo.final]];
    const key = `${combo.detector}|${combo.pausa}|${combo.reconhecimento}|${combo.traducao}|${combo.final}`;
    let unavailable = false;
    for (const [aria, value] of steps) {
      // Uma troca por vez: espera o painel terminar a anterior.
      await waitFor(a, `document.querySelector('.engines')?.dataset.ocupado === 'nao'`, "painel livre", 180000);
      const chosen = await waitFor(a, choose(aria, value), `escolher ${aria}`, 120000);
      if (chosen === "indisponivel") {
        unavailable = true;
        break;
      }
    }
    if (unavailable) {
      result.combinacoes.push({ nome: combo.nome, resultado: "indisponível neste computador" });
      continue;
    }
    const started = Date.now();
    await waitFor(a, `(() => { const e = document.querySelector('.engines'); return e?.dataset.escolha === ${JSON.stringify(key)} && e.dataset.ocupado === 'nao'; })()`, `combinação ${combo.nome}`, 180000);
    const loadSeconds = ((Date.now() - started) / 1000).toFixed(1);
    // O áudio repete a cada ~30 s: espera 3 frases com a combinação nova (as médias zeram na troca).
    const stats = await waitFor(a, `(() => { const e = document.querySelector('.engines'); return Number(e?.dataset.frases) >= 3 ? [...document.querySelectorAll('.engines__stats dl > div')].map(d => d.textContent.replace(/\\s+/g, ' ').trim()) : null; })()`, `médias (${combo.nome})`, 150000).catch(() => null);
    const problem = await a.evaluate(`document.querySelector('.engines__status--problem')?.textContent.trim() || null`);
    const lines = await a.evaluate(`[...document.querySelectorAll('.conversation .bubble--me:not(.bubble--partial)')].slice(0, 3).map(b => (b.querySelector('.bubble__main')?.textContent.trim() || '') + ' ⇒ ' + (b.querySelector('.bubble__secondary')?.textContent.trim() || ''))`);
    result.combinacoes.push({ nome: combo.nome, carregou_em_s: loadSeconds, medias: stats, problema: problem, ultimas: lines });
    if (process.env.E2E_SHOTS) {
      await a.screenshot(`${process.env.E2E_SHOTS}-motores-${combo.nome}.png`);
    }
  }
  result.ok = result.combinacoes.some((c) => c.medias);
} catch (error) {
  result.ok = false;
  result.falha = String(error);
}
a.close();
b.close();
console.log(JSON.stringify(result, null, 2));
process.exitCode = result.ok ? 0 : 1;
