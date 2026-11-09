// Supressão de ruído no teste de dispositivos, com o microfone simulado tocando fala + ruído
// (ruido.ps1). Para cada modo, junta o volume que sai da cadeia (o medidor do painel) por dois
// ciclos do áudio: o percentil 25 é o ruído entre as frases; o 90, a fala. O medidor vai de
// -80 dB a 0 dB: ruído abaixo de -80 dB aparece como -80.
// Chamado por ruido.ps1: node ruido.mjs <porta>
const [port] = process.argv.slice(2).map(Number);

async function connect(port) {
  for (let attempt = 0; attempt < 60; attempt++) {
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
          // userGesture: conta como clique da pessoa (a janela flutuante exige).
          const m = await send("Runtime.evaluate", { expression, awaitPromise: true, returnByValue: true, userGesture: true });
          if (m.result?.exceptionDetails) {
            throw new Error(JSON.stringify(m.result.exceptionDetails));
          }
          return m.result?.result?.value;
        };
        // Imagem só do conteúdo do app (a interface), nunca da tela do computador.
        const screenshot = async (path) => {
          const m = await send("Page.captureScreenshot", { format: "png" });
          const { writeFileSync } = await import("node:fs");
          writeFileSync(path, Buffer.from(m.result.data, "base64"));
        };
        // Janela menor que a tela: confere se o palco acompanha o tamanho.
        const resize = (width, height) => width
          ? send("Emulation.setDeviceMetricsOverride", { width, height, deviceScaleFactor: 1, mobile: false })
          : send("Emulation.clearDeviceMetricsOverride", {});
        return { evaluate, screenshot, resize, close: () => socket.close() };
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
    await new Promise((r) => setTimeout(r, 250));
  }
  throw new Error("tempo esgotado: " + what);
};

const click = (label) => `(() => { const b = [...document.querySelectorAll('button')].find(b => b.textContent.trim().startsWith(${JSON.stringify(label)})); if (!b || b.disabled) return false; b.click(); return true; })()`;
// Clica de novo enquanto o painel não aceitou a escolha (ele ignora cliques durante uma troca);
// termina quando o modo pedido está rodando e o painel está livre.
const selectMode = (name, mode) => `(() => {
  const panel = document.querySelector('.noise');
  const free = !document.querySelector('.noise__mode:disabled');
  if (panel?.dataset.ativo === ${JSON.stringify(mode)} && panel.dataset.modo === ${JSON.stringify(mode)} && free) return true;
  if (free && panel?.dataset.modo !== ${JSON.stringify(mode)}) [...document.querySelectorAll('.noise__mode')].find(m => m.textContent.trim().startsWith(${JSON.stringify(name)}))?.click();
  return false;
})()`;
const setRange = (aria, value) => `(() => { const r = document.querySelector('input[aria-label="${aria}"]'); if (!r || r.disabled) return false; r.value = ${JSON.stringify(String(value))}; r.dispatchEvent(new Event('change', { bubbles: true })); return true; })()`;
const setCheck = (aria, checked) => `(() => {
  const c = [...document.querySelectorAll('.noise__check input')].find(c => (c.getAttribute('aria-label') || c.parentElement.textContent).trim().startsWith(${JSON.stringify(aria)}));
  const free = !document.querySelector('.noise__mode:disabled');
  if (!c || !free) return false;
  if (c.checked === ${checked}) return !!document.querySelector('.noise')?.dataset.ativo;
  c.click();
  return false;
})()`;
const idle = `!document.querySelector('.noise__mode:disabled')`;

const CYCLE_MS = 12500;
const percentile = (values, p) => {
  const sorted = [...values].sort((x, y) => x - y);
  return sorted[Math.min(sorted.length - 1, Math.floor(sorted.length * p))];
};
const toDb = (percent) => Math.round((percent * 0.8 - 80) * 10) / 10;

async function measure(app, ms) {
  return app.evaluate(`new Promise((resolve) => {
    // Leitura a cada 50 ms (o medidor é pintado a cada 50 ms): silêncio parado também conta.
    const fill = document.querySelector('.noise .meter__fill');
    const values = [];
    const timer = setInterval(() => values.push(parseFloat(fill.style.width) || 0), 50);
    setTimeout(() => { clearInterval(timer); resolve(values); }, ${ms});
  })`);
}

const result = { modos: [] };
const app = await connect(port);
try {
  await waitFor(app, `!!window.Blazor`, "Blazor", 60000);
  await app.evaluate(`Blazor.navigateTo('/configuracoes/dispositivos')`);
  await waitFor(app, `!!document.querySelector('.noise .meter__fill')`, "painel de ruído", 30000);
  result.padrao = await waitFor(app, `document.querySelector('.noise')?.dataset.ativo || null`, "supressor ativo na prévia", 30000);
  // Sem o ganho automático do navegador: o volume não muda sozinho entre um modo e outro.
  await waitFor(app, setCheck("Ganho automático", false), "desligar o ganho automático", 30000);
  await new Promise((r) => setTimeout(r, 1000));

  const runs = [
    ["Desligada", "desligado", null],
    ["Padrão", "navegador", null],
    ["RNNoise", "rnnoise", null],
    ["GTCRN", "gtcrn", null],
    ["DeepFilterNet3", "deepfilter", 20],
    ["DeepFilterNet3", "deepfilter", 60],
    ["DeepFilterNet3", "deepfilter", 100],
  ];
  let baseline = null;
  for (const [name, mode, intensity] of runs) {
    await waitFor(app, selectMode(name, mode), `modo ${name} rodando`, 40000);
    if (intensity !== null) {
      await waitFor(app, setRange("Intensidade da supressão", intensity), `intensidade ${intensity}`);
      await waitFor(app, `${idle} && document.querySelector('.noise__label strong')?.textContent === '${intensity}%'`, `intensidade ${intensity} aplicada`, 30000);
    }
    await new Promise((r) => setTimeout(r, 1500));
    const values = await measure(app, CYCLE_MS * 2);
    const noise = toDb(percentile(values, 0.25));
    const speech = toDb(percentile(values, 0.9));
    baseline ??= { noise, speech };
    result.modos.push({
      modo: intensity === null ? name : `${name} ${intensity}%`,
      ruido_db: noise,
      fala_db: speech,
      fala_acima_do_ruido_db: Math.round((speech - noise) * 10) / 10,
      ruido_removido_db: Math.round((baseline.noise - noise) * 10) / 10,
      fala_perdida_db: Math.round((baseline.speech - speech) * 10) / 10,
      leituras: values.length,
    });
  }

  // Sensibilidade manual e "ouvir meu microfone".
  await waitFor(app, setCheck("Sensibilidade automática", false), "sensibilidade manual", 15000);
  result.limite_manual = await waitFor(app, `(() => { const r = document.querySelector('input[aria-label^="Limite da sensibilidade"]'); return r ? r.value : null; })()`, "controle do limite");
  await waitFor(app, setRange("Limite da sensibilidade, em decibéis", -35), "limite -35 dB");
  result.marca_do_limite = await waitFor(app, `(() => { const m = document.querySelector('.noise .meter__mark'); return m && m.style.left === '56%' ? m.style.left : null; })()`, "marca em -35 dB", 10000).catch(() => null);
  await waitFor(app, setCheck("Sensibilidade automática", true), "sensibilidade automática", 15000);
  await waitFor(app, click("Ouvir meu microfone"), "ouvir o microfone");
  result.ouvindo = await waitFor(app, `document.querySelector('.devices__monitor')?.getAttribute('aria-pressed') === 'true'`, "ouvindo", 10000);
  if (process.env.E2E_SHOTS) {
    await app.screenshot(`${process.env.E2E_SHOTS}-dispositivos.png`);
  }
  await waitFor(app, click("Parar de ouvir"), "parar de ouvir");
  result.ok = result.modos.length === runs.length;
} catch (error) {
  result.ok = false;
  result.falha = String(error);
}
app.close();
console.log(JSON.stringify(result, null, 2));
process.exitCode = result.ok ? 0 : 1;
