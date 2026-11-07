// Teste de ponta a ponta: dois apps no mesmo computador trocam convite e resposta pela
// interface (lendo e preenchendo os campos de texto) e conectam.
// Chamado por chamada.ps1: node chamada.mjs <portaA> <portaB>
const [portA, portB] = process.argv.slice(2).map(Number);

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
          const m = await send("Runtime.evaluate", { expression, awaitPromise: true, returnByValue: true });
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
    await new Promise((r) => setTimeout(r, 250));
  }
  throw new Error("tempo esgotado: " + what);
};

const click = (label) => `(() => { const b = [...document.querySelectorAll('button')].find(b => b.textContent.trim().startsWith(${JSON.stringify(label)})); if (!b || b.disabled) return false; b.click(); return true; })()`;
const fill = (aria, value) => `(() => { const t = document.querySelector('textarea[aria-label="${aria}"]'); t.value = ${JSON.stringify(value)}; t.dispatchEvent(new Event('input', { bubbles: true })); return true; })()`;
const read = (selector) => `document.querySelector(${JSON.stringify(selector)})?.value || document.querySelector(${JSON.stringify(selector)})?.textContent || null`;

const result = {};
const a = await connect(portA);
const b = await connect(portB);
try {
  await waitFor(a, click("Nova chamada"), "botão Nova chamada (A)");
  const invite = await waitFor(a, `(${read('textarea[aria-label="Código de convite"]')} || '').startsWith('DT1.') && ${read('textarea[aria-label="Código de convite"]')}`, "convite");
  result.tamanho_convite = invite.length;

  await waitFor(b, click("Entrar com código"), "botão Entrar com código (B)");
  await waitFor(b, `!!document.querySelector('textarea[aria-label="Código de convite"]')`, "campo do convite (B)");
  await b.evaluate(fill("Código de convite", invite));
  await waitFor(b, click("Gerar resposta"), "Gerar resposta");
  const answer = await waitFor(b, `(${read('textarea[aria-label="Código de resposta"]')} || '').startsWith('DT1.') && ${read('textarea[aria-label="Código de resposta"]')}`, "resposta");
  result.tamanho_resposta = answer.length;
  result.b_ve_nome_do_anfitriao = await b.evaluate(`document.querySelector('.handshake__hint strong')?.textContent`);

  // Resposta colada duas vezes: o convite é de uso único.
  await a.evaluate(fill("Código de resposta", answer));
  await waitFor(a, click("Conectar"), "Conectar");

  const codeA = await waitFor(a, `document.querySelector('.call__digits')?.textContent`, "código de segurança (A)");
  const codeB = await waitFor(b, `document.querySelector('.call__digits')?.textContent`, "código de segurança (B)");
  result.codigo_seguranca_a = codeA;
  result.codigo_seguranca_b = codeB;
  result.codigos_iguais = codeA === codeB;
  result.a_ve = await a.evaluate(`document.querySelector('.call__peer')?.textContent.trim()`);
  result.b_ve = await b.evaluate(`document.querySelector('.call__peer')?.textContent.trim()`);
  result.ping_a = await waitFor(a, `document.querySelector('.call__route')?.textContent.trim()`, "ping (A)", 15000);
  result.problema_a = await a.evaluate(`document.querySelector('.call__problem')?.textContent.trim() || null`);
  result.problema_b = await b.evaluate(`document.querySelector('.call__problem')?.textContent.trim() || null`);

  await new Promise((r) => setTimeout(r, 4000));
  result.faixas_remotas_b = await b.evaluate(`document.querySelector('.tile--other video')?.srcObject?.getTracks().map(t => t.kind).join(',') || ''`);

  // Tela 2K com vídeo sintético: resultado previsível e nada da tela real.
  await a.evaluate(`(async () => {
    const canvas = document.createElement('canvas');
    canvas.width = 2560;
    canvas.height = 1440;
    const context = canvas.getContext('2d');
    let frame = 0;
    window.__telaSintetica = setInterval(() => {
      context.fillStyle = '#1b1916';
      context.fillRect(0, 0, 2560, 1440);
      context.fillStyle = '#f2b705';
      context.font = '120px monospace';
      context.fillText('tela 2K de teste · ' + frame++, 120, 260);
      context.fillStyle = '#f1ece2';
      context.font = '28px monospace';
      for (let line = 0; line < 30; line++) context.fillText('linha de texto pequeno para conferir a nitidez ' + line, 120, 360 + line * 34);
    }, 33);
    const media = await import('./js/call.js');
    await media.shareStream(canvas.captureStream(30), 'detail');
    return true;
  })()`);
  await waitFor(b, `!!document.querySelector('.call__stage--screen')`, "palco com a tela (B)");
  result.tela_banner_a = await waitFor(a, `document.querySelector('.call__sharing')?.textContent.replace(/\\s+/g, ' ').trim()`, "faixa de compartilhamento (A)");
  result.tela_recebida_b = await waitFor(b, `(() => { const t = document.querySelector('.screen-tile figcaption')?.textContent.replace(/\\s+/g, ' ').trim(); return t && t.includes('2560×1440') ? t : null; })()`, "tela 2560×1440 recebida (B)", 40000);
  if (process.env.E2E_SHOTS) {
    await b.screenshot(`${process.env.E2E_SHOTS}-tela-recebida.png`);
    await a.screenshot(`${process.env.E2E_SHOTS}-tela-enviada.png`);
  }
  await waitFor(a, click("Fluida"), "modo fluido");
  result.tela_modo_fluido = await waitFor(a, `[...document.querySelectorAll('.segmented__option')].find(b => b.textContent.includes('Fluida'))?.getAttribute('aria-pressed') === 'true'`, "modo fluido marcado");
  await waitFor(a, click("Parar"), "parar tela");
  await waitFor(b, `!document.querySelector('.call__stage--screen')`, "palco sem a tela (B)");
  result.tela_parou = true;
  await a.evaluate(`clearInterval(window.__telaSintetica)`);

  // Captura real, escolhendo sozinha a janela do próprio app (nunca a tela inteira).
  await waitFor(a, click("Compartilhar tela"), "compartilhar tela de verdade");
  result.tela_real = await waitFor(b, `(() => { const t = document.querySelector('.screen-tile figcaption')?.textContent; return !!document.querySelector('.call__stage--screen') && t && t.includes('×') ? t.replace(/\\s+/g, ' ').trim() : null; })()`, "captura real", 20000).catch(() => "não confirmada (o seletor do Windows pode ter aberto)");
  await a.evaluate(click("Parar tela"));

  if (process.env.E2E_HANGUP !== "0") {
    await a.evaluate(click("Encerrar"));
    result.b_aviso_encerramento = await waitFor(b, `document.querySelector('.call__problem')?.textContent.includes('encerrou') && document.querySelector('.call__problem').textContent.trim()`, "aviso de encerramento (B)");
  }
  result.ok = result.codigos_iguais;
} catch (error) {
  result.ok = false;
  result.falha = String(error);
}
a.close();
b.close();
console.log(JSON.stringify(result, null, 2));
process.exitCode = result.ok ? 0 : 1;
