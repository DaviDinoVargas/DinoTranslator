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
        const evaluate = (expression) => new Promise((resolve, reject) => {
          const messageId = ++id;
          pending.set(messageId, (m) => (m.result?.exceptionDetails ? reject(new Error(JSON.stringify(m.result.exceptionDetails))) : resolve(m.result?.result?.value)));
          socket.send(JSON.stringify({ id: messageId, method: "Runtime.evaluate", params: { expression, awaitPromise: true, returnByValue: true } }));
        });
        return { evaluate, close: () => socket.close() };
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
