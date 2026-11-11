// Versão Plus de ponta a ponta (chamado por plus.ps1): node plus.mjs <portaDino> <portaAnna>
const [portA, portB] = process.argv.slice(2).map(Number);
const password = process.env.E2E_SENHA;

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

const click = (label, scope = "") => `(() => { const b = [...document.querySelectorAll('${scope} button')].find(b => (b.getAttribute('aria-label') || b.textContent).trim().startsWith(${JSON.stringify(label)})); if (!b || b.disabled) return false; b.click(); return true; })()`;
const type = (aria, value) => `(() => { const i = document.querySelector('[aria-label="${aria}"]'); if (!i || i.disabled) return false; i.value = ${JSON.stringify(value)}; i.dispatchEvent(new Event('input', { bubbles: true })); i.dispatchEvent(new Event('change', { bubbles: true })); return true; })()`;
const submit = (aria) => `(() => { const i = document.querySelector('[aria-label="${aria}"]'); const f = i?.closest('form'); if (!f) return false; f.requestSubmit(); return true; })()`;
const text = (selector) => `document.querySelector(${JSON.stringify(selector)})?.textContent.replace(/\\s+/g, ' ').trim() || null`;
const messages = `[...document.querySelectorAll('.chat-message')].map(m => ({ autor: m.querySelector('.chat-message__author')?.textContent.trim(), texto: m.querySelector('.chat-message__text')?.textContent.trim(), original: m.querySelector('.chat-message__original')?.textContent.trim() || null }))`;

async function login(app, user) {
  await waitFor(app, `!!window.Blazor`, "Blazor", 60000);
  await app.evaluate(`Blazor.navigateTo('/conta')`);
  await waitFor(app, type("Usuário", user), `usuário (${user})`, 30000);
  await waitFor(app, type("Senha", password), `senha (${user})`);
  await waitFor(app, submit("Senha"), `entrar (${user})`);
  await waitFor(app, `!!document.querySelector('.plus')`, `tela Plus (${user})`, 30000);
  await waitFor(app, `document.querySelector('.plus')?.dataset.conectado === 'sim'`, `tempo real (${user})`, 30000);
}

const result = {};
const a = await connect(portA);
const b = await connect(portB);
try {
  await login(a, "dino");
  await login(b, "anna");

  // Dino cria o grupo e copia o convite.
  await waitFor(a, click("Criar ou entrar num grupo"), "abrir criar grupo");
  await waitFor(a, type("Nome do grupo", "Família"), "nome do grupo");
  await waitFor(a, submit("Nome do grupo"), "criar grupo");
  result.grupo_a = await waitFor(a, `document.querySelector('.plus__side-title')?.textContent.trim() === 'Família' && 'Família'`, "grupo aberto (A)", 20000);
  await waitFor(a, click("Convidar"), "convidar");
  const notice = await waitFor(a, text(".plus__notice span"), "convite", 15000);
  const code = /Convite(?: copiado)?: ([a-z0-9]{10})/.exec(notice)?.[1];
  result.convite = code;

  // Anna entra pelo convite.
  await waitFor(b, click("Criar ou entrar num grupo"), "abrir entrar no grupo (B)");
  await waitFor(b, type("Código do convite", `Entra aí: ${code}`), "código (B)");
  await waitFor(b, submit("Código do convite"), "entrar no grupo (B)");
  result.grupo_b = await waitFor(b, `document.querySelector('.plus__side-title')?.textContent.trim() === 'Família' && 'Família'`, "grupo aberto (B)", 20000);
  result.membros_a = await waitFor(a, `(() => { const n = document.querySelectorAll('.plus__members .person').length; return n >= 2 ? n : 0; })()`, "Anna nos membros (A)", 20000);

  // Conversa no #geral: cada um lê no próprio idioma.
  await waitFor(a, `document.querySelector('.chat__title')?.textContent.includes('#geral')`, "#geral aberto (A)", 15000);
  await waitFor(b, `document.querySelector('.chat__title')?.textContent.includes('#geral')`, "#geral aberto (B)", 15000);
  await waitFor(a, type("Escrever mensagem", "Oi, Anna! Tudo bem?"), "escrever (A)");
  await waitFor(a, submit("Escrever mensagem"), "enviar (A)");
  result.chat_em_b = await waitFor(b, `(() => { const m = ${messages}.find(m => m.original?.includes('Oi, Anna')); return m?.texto?.startsWith('[ru-RU]') ? m : null; })()`, "mensagem traduzida (B)", 20000);
  await waitFor(b, type("Escrever mensagem", "Привет, Дино!"), "escrever (B)");
  await waitFor(b, submit("Escrever mensagem"), "enviar (B)");
  result.chat_em_a = await waitFor(a, `(() => { const m = ${messages}.find(m => m.original?.includes('Привет')); return m?.texto?.startsWith('[pt-BR]') ? m : null; })()`, "resposta traduzida (A)", 20000);
  if (process.env.E2E_SHOTS) {
    await a.screenshot(`${process.env.E2E_SHOTS}-plus-grupo-dino.png`);
    await b.screenshot(`${process.env.E2E_SHOTS}-plus-grupo-anna.png`);
  }

  // Amizade e conversa direta.
  await waitFor(a, click("Amigos e conversas"), "início (A)");
  await waitFor(a, type("Adicionar amigo pelo usuário", "anna"), "adicionar amigo (A)");
  await waitFor(a, submit("Adicionar amigo pelo usuário"), "pedir amizade (A)");
  await waitFor(b, click("Amigos e conversas"), "início (B)");
  await waitFor(b, click("Aceitar Dino"), "aceitar (B)", 20000);
  await waitFor(b, click("Conversar com Dino"), "abrir conversa (B)", 20000);
  await waitFor(b, `!!document.querySelector('.chat')`, "conversa aberta (B)", 15000);
  await waitFor(b, type("Escrever mensagem", "Como está o tempo aí?"), "escrever direta (B)");
  await waitFor(b, submit("Escrever mensagem"), "enviar direta (B)");
  await waitFor(a, `!!document.querySelector('.person--unread') || !!document.querySelector('.person--on')`, "conversa nova (A)", 20000);
  await waitFor(a, `(() => { const p = [...document.querySelectorAll('.person--button')].find(p => p.textContent.includes('Anna')); if (!p) return false; p.click(); return true; })()`, "abrir conversa (A)");
  result.direta_em_a = await waitFor(a, `(() => { const m = ${messages}.find(m => m.original?.includes('Como está')); return m?.texto?.startsWith('[pt-BR]') ? m : null; })()`, "mensagem direta (A)", 20000);
  result.amigo_online_a = await a.evaluate(`!!document.querySelector('.presence--on')`);
  if (process.env.E2E_SHOTS) {
    await a.screenshot(`${process.env.E2E_SHOTS}-plus-direta-dino.png`);
  }
  result.ok = true;
} catch (error) {
  result.ok = false;
  result.falha = String(error);
  if (process.env.E2E_SHOTS) {
    await a.screenshot(`${process.env.E2E_SHOTS}-plus-falha-a.png`).catch(() => {});
    await b.screenshot(`${process.env.E2E_SHOTS}-plus-falha-b.png`).catch(() => {});
  }
}
a.close();
b.close();
console.log(JSON.stringify(result, null, 2));
process.exitCode = result.ok ? 0 : 1;
