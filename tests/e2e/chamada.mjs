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
  await waitFor(b, click("Entrar na chamada"), "Entrar na chamada");
  const answeredAt = Date.now();
  const answer = await waitFor(b, `(${read('textarea[aria-label="Código de resposta"]')} || '').startsWith('DT1.') && ${read('textarea[aria-label="Código de resposta"]')}`, "resposta");
  result.tamanho_resposta = answer.length;
  result.b_ve_nome_do_anfitriao = await b.evaluate(`document.querySelector('.handshake__hint strong')?.textContent`);

  if (process.env.E2E_MANUAL === "1") {
    // Sem o encontro automático: a resposta vai "pelo mensageiro" e é colada no convite.
    await a.evaluate(fill("Código de resposta", answer));
    await waitFor(a, click("Conectar"), "Conectar");
    result.modo = "resposta colada";
  } else {
    // Encontro automático: ninguém cola nada no anfitrião.
    result.modo = "automático";
    result.b_status = await b.evaluate(`document.querySelector('.handshake__hint')?.textContent.trim()`);
  }

  const codeA = await waitFor(a, `document.querySelector('.call__digits')?.textContent`, "código de segurança (A)");
  const codeB = await waitFor(b, `document.querySelector('.call__digits')?.textContent`, "código de segurança (B)");
  result.segundos_ate_conectar = ((Date.now() - answeredAt) / 1000).toFixed(1);
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
  // A prévia da própria câmera nunca toca o microfone (senão a pessoa se ouve e vira eco).
  result.previa_muda_a = await a.evaluate(`(() => { const v = document.querySelector('.tile--me video'); return v.muted === true && v.srcObject?.getAudioTracks().length === 0 && v.srcObject?.getVideoTracks().length === 1; })()`);

  // Tradução: só o Dino (anfitrião) tem motor, o simulado; a Anna recebe tudo pela chamada.
  const captionText = (selector) => `document.querySelector('${selector}')?.textContent.replace(/\\s+/g, ' ').trim() || null`;
  result.traducao_a = await waitFor(a, captionText(".conversation__where"), "aviso de tradução (A)");
  result.traducao_b = await waitFor(b, captionText(".conversation__where"), "aviso de tradução (B)");
  result.legenda_da_anna_em_a = await waitFor(a, captionText(".caption--other .caption__main"), "legenda da Anna em A", 20000);
  result.legenda_do_dino_em_b = await waitFor(b, captionText(".caption--other .caption__main"), "legenda do Dino em B", 20000);
  result.legenda_propria_em_b = await waitFor(b, captionText(".caption--me .caption__main"), "legenda da própria Anna em B", 20000);
  if (process.env.E2E_SHOTS) {
    await b.screenshot(`${process.env.E2E_SHOTS}-legendas-anna.png`);
    await a.screenshot(`${process.env.E2E_SHOTS}-legendas-dino.png`);
  }

  // Conversa: as falas finais viram bolhas no painel lateral.
  result.conversa_bolhas_b = await waitFor(b, `(() => { const n = document.querySelectorAll('.conversation .bubble:not(.bubble--partial)').length; return n >= 2 ? n : 0; })()`, "bolhas na conversa (B)", 20000);
  result.conversa_outra_b = await b.evaluate(`document.querySelector('.conversation .bubble--other .bubble__main')?.textContent.trim() || null`);

  // Supressão de ruído (A): o DeepFilterNet3 roda por padrão; cada modo é trocado no meio da
  // chamada e precisa ficar rodando de fato, com o medidor mexendo.
  await waitFor(a, click("Ruído"), "botão Ruído (A)");
  result.ruido_padrao_a = await waitFor(a, `document.querySelector('.noise')?.dataset.ativo || null`, "supressor ativo (A)", 30000);
  result.ruido_medidor_a = await waitFor(a, `(() => { const f = document.querySelector('.noise .meter__fill'); return f && parseFloat(f.style.width) > 0 ? f.style.width : null; })()`, "medidor do ruído (A)", 10000).catch(() => null);
  for (const [nome, modo] of [["GTCRN", "gtcrn"], ["RNNoise", "rnnoise"], ["Padrão", "navegador"], ["Desligada", "desligado"], ["DeepFilterNet3", "deepfilter"]]) {
    await waitFor(a, `(() => { const m = [...document.querySelectorAll('.noise__mode')].find(m => m.textContent.trim().startsWith(${JSON.stringify(nome)})); if (!m || m.disabled) return false; m.click(); return true; })()`, `modo ${nome} (A)`, 20000);
    result[`ruido_${modo}_a`] = await waitFor(a, `document.querySelector('.noise')?.dataset.ativo === ${JSON.stringify(modo)} && !document.querySelector('.noise__mode:disabled') ? document.querySelector('.noise__status').textContent.trim() : null`, `modo ${nome} rodando (A)`, 30000);
  }
  // A fala do Dino continua chegando legendada depois das trocas.
  const bubblesBefore = await b.evaluate(`document.querySelectorAll('.conversation .bubble--other:not(.bubble--partial)').length`);
  result.legenda_depois_das_trocas_b = await waitFor(b, `document.querySelectorAll('.conversation .bubble--other:not(.bubble--partial)').length > ${bubblesBefore}`, "legenda do Dino depois das trocas (B)", 30000);
  if (process.env.E2E_SHOTS) {
    await a.screenshot(`${process.env.E2E_SHOTS}-ruido.png`);
  }
  await waitFor(a, `(() => { const c = document.querySelector('.engines--ruido .conversation__close'); if (!c) return false; c.click(); return true; })()`, "fechar o painel de ruído (A)");

  // Chat escrito: a Anna escreve, quem traduz (o Dino) devolve a tradução aos dois.
  const write = (text) => `(() => { const i = document.querySelector('.composer__input'); if (!i || i.disabled) return false; i.value = ${JSON.stringify(text)}; i.dispatchEvent(new Event('input', { bubbles: true })); return true; })()`;
  const send = `(() => { const b = document.querySelector('.composer__send'); if (!b || b.disabled) return false; b.click(); return true; })()`;
  const written = (side) => `(() => { const b = [...document.querySelectorAll('.conversation .bubble--${side}')].find(x => x.querySelector('.bubble__meta')?.textContent.includes('escrita')); return b ? { principal: b.querySelector('.bubble__main')?.textContent.trim(), secundario: b.querySelector('.bubble__secondary')?.textContent.trim() || null, rodape: b.querySelector('.bubble__meta')?.textContent.replace(/\\s+/g, ' ').trim() } : null; })()`;
  await waitFor(b, write("Привет, Дино!"), "escrever (B)");
  await waitFor(b, send, "enviar (B)");
  result.chat_anna_em_b = await waitFor(b, `(() => { const r = ${written("me")}; return r?.secundario?.startsWith('[pt-BR]') ? r : null; })()`, "mensagem traduzida (B)", 10000).catch(() => null);
  result.chat_anna_em_a = await waitFor(a, `(() => { const r = ${written("other")}; return r?.principal?.startsWith('[pt-BR]') ? r : null; })()`, "mensagem da Anna (A)", 10000).catch(() => null);
  await waitFor(a, write("Oi, Anna! Tudo bem?"), "escrever (A)");
  await waitFor(a, send, "enviar (A)");
  result.chat_dino_em_b = await waitFor(b, `(() => { const r = ${written("other")}; return r?.principal?.startsWith('[ru-RU]') ? r : null; })()`, "mensagem do Dino (B)", 10000).catch(() => null);

  // Correção: a Anna conserta a mensagem dela e a nova tradução chega aos dois.
  await b.evaluate(`[...document.querySelectorAll('.conversation .bubble--me')].find(x => x.querySelector('.bubble__meta')?.textContent.includes('escrita'))?.querySelector('.bubble__edit-button')?.click()`);
  await waitFor(b, `(() => { const t = document.querySelector('.bubble__input'); if (!t) return false; t.value = 'Привет, Дино! Как дела?'; t.dispatchEvent(new Event('input', { bubbles: true })); return true; })()`, "campo de correção (B)", 5000);
  await waitFor(b, `(() => { const s = document.querySelector('.bubble__save'); if (!s || s.disabled) return false; s.click(); return true; })()`, "corrigir (B)", 5000);
  result.correcao_em_a = await waitFor(a, `(() => { const r = ${written("other")}; return r?.principal?.includes('Как дела') && r.rodape.includes('editada') ? r : null; })()`, "correção da Anna (A)", 10000).catch(() => null);
  result.correcao_em_b = await waitFor(b, `(() => { const r = ${written("me")}; return r?.secundario?.includes('Как дела') && r.rodape.includes('editada') ? r : null; })()`, "correção traduzida (B)", 10000).catch(() => null);
  if (process.env.E2E_SHOTS) {
    await b.screenshot(`${process.env.E2E_SHOTS}-chat-anna.png`);
    await a.screenshot(`${process.env.E2E_SHOTS}-chat-dino.png`);
  }

  // Palco com a janela pequena: a outra pessoa continua inteira e a sua câmera no canto.
  const layout = `(() => {
    const box = (s) => { const r = document.querySelector(s)?.getBoundingClientRect(); return r ? [Math.round(r.width), Math.round(r.height)] : null; };
    return { palco: box('.call__stage'), outra: box('.tile--other'), voce: box('.tile--me'), conversa: box('.conversation'), rolagem: document.documentElement.scrollWidth > innerWidth };
  })()`;
  result.layout_1280 = await b.evaluate(layout);
  await b.resize(760, 520);
  await new Promise((r) => setTimeout(r, 500));
  result.layout_760 = await b.evaluate(layout);
  if (process.env.E2E_SHOTS) {
    await b.screenshot(`${process.env.E2E_SHOTS}-janela-pequena.png`);
  }
  await b.resize(0);

  // Qualidade do vídeo: troca no meio da chamada, sem renegociar nem aviso de problema.
  await a.evaluate(`(() => { const s = document.querySelector('.call__quality select'); s.value = 'Baixa'; s.dispatchEvent(new Event('change', { bubbles: true })); return true; })()`);
  await new Promise((r) => setTimeout(r, 1500));
  result.qualidade_a = await a.evaluate(`document.querySelector('.call__quality select').value`);
  result.qualidade_guardada_a = await a.evaluate(`localStorage.getItem('dt.qualidade')`);

  // Som da chamada desligado em B: o vídeo da Anna fica mudo, a legenda continua.
  await waitFor(b, click("Som da chamada"), "som da chamada (B)");
  result.som_desligado_b = await waitFor(b, `document.querySelector('.tile--other video').muted`, "som mudo (B)", 5000).catch(() => false);
  await waitFor(b, click("Som desligado"), "religar o som (B)");

  // Janela flutuante (exige clique da pessoa): abre e fecha pelo próprio botão.
  await waitFor(a, click("Janela flutuante"), "janela flutuante (A)");
  result.flutuante_aberta_a = await waitFor(a, `!!document.querySelector('.call__floating-note') && !!document.pictureInPictureElement`, "janela flutuante (A)", 5000).catch(() => false);
  if (process.env.E2E_SHOTS && result.flutuante_aberta_a) {
    await new Promise((r) => setTimeout(r, 1500));
    const png = await a.evaluate(`(() => { const v = document.pictureInPictureElement; const c = document.createElement('canvas'); c.width = v.videoWidth; c.height = v.videoHeight; c.getContext('2d').drawImage(v, 0, 0); return c.toDataURL('image/png').split(',')[1]; })()`);
    const { writeFileSync } = await import("node:fs");
    writeFileSync(`${process.env.E2E_SHOTS}-janela-flutuante.png`, Buffer.from(png, "base64"));
  }
  result.flutuante_problema_a =await a.evaluate(`document.querySelector('.call__problem')?.textContent.trim() || null`);
  await a.evaluate(click("Janela flutuante"));
  result.flutuante_fechada_a = await waitFor(a, `!document.querySelector('.call__floating-note')`, "janela flutuante fechada (A)", 5000).catch(() => false);

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
  // Quem compartilha vê a própria tela numa miniatura e pode pô-la em destaque.
  const tile = (selector) => `(() => { const f = document.querySelector('${selector}'); const v = f?.querySelector('video'); return f ? { classe: [...f.classList].find(c => c.startsWith('stage-tile--')), video: v.videoWidth + 'x' + v.videoHeight } : null; })()`;
  result.tela_propria_a = await waitFor(a, `(() => { const v = document.querySelector('.screen-tile--mine video'); return v.videoWidth > 0 && v.closest('figure').classList.contains('stage-tile--thumb') ? v.videoWidth + 'x' + v.videoHeight : null; })()`, "prévia da própria tela (A)", 10000).catch(() => null);
  if (process.env.E2E_SHOTS) {
    await b.screenshot(`${process.env.E2E_SHOTS}-tela-recebida.png`);
    await a.screenshot(`${process.env.E2E_SHOTS}-tela-enviada.png`);
  }
  await a.evaluate(`document.querySelector('.screen-tile--mine .stage-tile__pick').click()`);
  result.tela_propria_destaque_a = await waitFor(a, `document.querySelector('.screen-tile--mine').classList.contains('stage-tile--main')`, "própria tela em destaque (A)", 5000).catch(() => false);

  // As duas pessoas mostrando a tela ao mesmo tempo: a nova vai para o destaque e dá para escolher.
  await b.evaluate(`(async () => {
    const canvas = document.createElement('canvas');
    canvas.width = 1280;
    canvas.height = 720;
    const context = canvas.getContext('2d');
    window.__telaSinteticaB = setInterval(() => {
      context.fillStyle = '#0a3a6a';
      context.fillRect(0, 0, 1280, 720);
      context.fillStyle = '#fff';
      context.font = '64px sans-serif';
      context.fillText('tela da Anna ' + Date.now(), 80, 200);
    }, 33);
    const media = await import('./js/call.js');
    await media.shareStream(canvas.captureStream(30), 'detail');
    return true;
  })()`);
  result.duas_telas_a = await waitFor(a, `(() => { const outra = document.querySelector('.screen-tile:not(.screen-tile--mine)'); const minha = document.querySelector('.screen-tile--mine'); return outra.classList.contains('stage-tile--main') && minha.classList.contains('stage-tile--thumb') && outra.querySelector('video').videoWidth > 0; })()`, "tela da Anna em destaque com as duas (A)", 20000).catch(() => false);
  result.palco_duas_telas_a = { outra: await a.evaluate(tile('.screen-tile:not(.screen-tile--mine)')), minha: await a.evaluate(tile('.screen-tile--mine')), camera: await a.evaluate(tile('.tile--other')), voce: await a.evaluate(tile('.tile--me')) };
  await b.evaluate(`document.querySelector('.tile--other .stage-tile__pick').click()`);
  result.camera_escolhida_b = await waitFor(b, `document.querySelector('.tile--other').classList.contains('stage-tile--main') && document.querySelector('.screen-tile--mine').classList.contains('stage-tile--thumb')`, "câmera do Dino em destaque (B)", 5000).catch(() => false);
  if (process.env.E2E_SHOTS) {
    await a.screenshot(`${process.env.E2E_SHOTS}-duas-telas-dino.png`);
    await b.screenshot(`${process.env.E2E_SHOTS}-duas-telas-anna.png`);
  }
  await waitFor(b, click("Parar"), "parar tela (B)");
  await b.evaluate(`clearInterval(window.__telaSinteticaB)`);
  await waitFor(a, `document.querySelector('.screen-tile:not(.screen-tile--mine)').classList.contains('stage-tile--hidden')`, "tela da Anna saiu (A)", 10000);
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
    result.b_aviso_encerramento = await waitFor(b, `document.querySelector('.call__problem')?.textContent.includes('encerrou') && document.querySelector('.call__problem').textContent.trim()`, "aviso de encerramento (B)").catch(async (error) => { result.b_depois_de_encerrar = await b.evaluate(`({ url: location.pathname, problema: document.querySelector('.call__problem')?.textContent.trim() || null, botoes: [...document.querySelectorAll('.call__dock button')].map(x => x.textContent.trim() + (x.disabled ? ' (off)' : '')).join(' | ') })`); result.a_depois_de_encerrar = await a.evaluate(`location.pathname`); throw error; });
  }
  result.ok = result.codigos_iguais
    && result.previa_muda_a === true
    && result.chat_anna_em_b !== null
    && result.chat_anna_em_a !== null
    && result.chat_dino_em_b !== null
    && result.correcao_em_a !== null
    && result.correcao_em_b !== null
    && result.tela_propria_a !== null
    && result.tela_propria_destaque_a === true
    && result.duas_telas_a === true
    && result.camera_escolhida_b === true
    && result.conversa_bolhas_b >= 2
    && !result.layout_760?.rolagem
    && result.qualidade_a === "Baixa"
    && result.som_desligado_b === true
    && result.flutuante_aberta_a === true
    && result.legenda_da_anna_em_a?.startsWith("[pt-BR]")
    && result.legenda_do_dino_em_b?.startsWith("[ru-RU]")
    && result.legenda_propria_em_b?.startsWith("[ru-RU]");
} catch (error) {
  result.ok = false;
  result.falha = String(error);
}
a.close();
b.close();
console.log(JSON.stringify(result, null, 2));
process.exitCode = result.ok ? 0 : 1;
