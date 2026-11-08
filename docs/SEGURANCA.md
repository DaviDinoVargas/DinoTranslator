# DinoTranslator — Segurança

Segurança é requisito, não acabamento: cada módulo só é integrado à `main` com os controles desta lista que lhe cabem.

O app tem dois modos de conexão. O **modo código** (padrão) não usa servidor; o **modo servidor** (opcional, desligado) mantém login e presença. As seções 2 a 4 valem para os dois modos; a seção 5, só para o modo servidor.

## 1. O que proteger

- **Conteúdo das conversas**: vídeo, áudio, tela, chat e falas.
- **Chaves dos serviços de reconhecimento e tradução**, no computador do anfitrião.
- **Os computadores dos dois**: o app não pode virar porta de entrada.

## 2. Ameaças consideradas

| Ameaça                                       | Exemplo                                                        | Controles |
|----------------------------------------------|----------------------------------------------------------------|-----------|
| Alguém entrar na chamada                     | Terceiro obtém o código de convite e responde antes do convidado | Convite de uso único com validade de 30 min; nome de quem respondeu na tela; código de segurança conferido em voz alta |
| Código adulterado no caminho (MITM)          | Troca da impressão digital DTLS dentro do código               | Código de segurança da chamada conferido em voz alta |
| Código malicioso                             | Texto enorme, compressão explosiva, campos inesperados         | Tamanho máximo antes e depois de descomprimir; validação estrita de cada campo; versão conhecida |
| Interceptação de rede                        | Wi-Fi público, provedor de internet                            | DTLS-SRTP na mídia e DTLS no canal de dados |
| Mensagem maliciosa pelo canal de dados       | HTML ou script no chat; mensagem gigante; tipo desconhecido    | Tamanho máximo; tipos conhecidos; todo texto renderizado como texto; CSP estrita |
| Navegação maliciosa no WebView2              | Link no chat abre página que tenta usar a câmera               | Navegação externa bloqueada; permissões só para a origem do app |
| Vazamento das chaves dos motores             | Chave lida do disco ou enviada pela chamada                    | Chaves cifradas com DPAPI; nunca trafegam pela chamada; nunca em log |
| Vazamento do histórico                       | Outra pessoa usa o computador                                  | Histórico cifrado com DPAPI; botão para apagar |
| Exposição do endereço de rede                | O código mostra o IP público a quem o vê                       | Códigos trocados só por mensageiro de confiança; IPs locais ocultos (mDNS do WebRTC) |
| Segredos no repositório                      | Chave de provedor em arquivo versionado                        | Segredos só no computador do usuário; varredura antes do push |
| Dependência vulnerável                       | Pacote NuGet com CVE                                           | Auditoria de pacotes no build (falha com vulnerabilidade) |
| Instalador adulterado                        | Arquivo trocado no caminho                                     | Assinatura de código; SHA-256 publicado |

## 3. Controles do modo código

### 3.1 Código da chamada (`cliente/codigo-chamada`)

- Formato `DT1.` + base64url de JSON comprimido; até 8 KB de texto e 32 KB descomprimido — acima disso é recusado sem processar.
- Campos obrigatórios e validados: versão, tipo (convite ou resposta), id da chamada (128 bits aleatórios), horário de criação, perfil (nome até 40 caracteres, idioma da lista, gênero, tratamento) e dados de conexão (credenciais ICE, impressão digital DTLS SHA-256, até 16 candidatos).
- Convite válido por 30 min e para uma única resposta; a resposta precisa trazer o mesmo id da chamada.
- **Código de segurança**: SHA-256 das duas impressões digitais DTLS (em ordem) → 12 dígitos exibidos para os dois.
- **Encontro automático** (a resposta volta sem o mensageiro e os dois computadores se procuram ao mesmo tempo, o que atravessa os roteadores domésticos): passa por um serviço público de mensagens (ntfy.sh por padrão; trocável ou desligável em `Encontro.Servidor` do appsettings.json, só HTTPS). Tópico e chave AES-256-GCM derivam por HKDF do id da chamada, que só existe nos códigos: o serviço vê um tópico aleatório, bytes cifrados e os endereços IP de quem conecta a ele. Mensagens de até 4 KB, linhas do fluxo de até 16 KB, tipo conhecido e o código dentro é validado como se tivesse sido colado. Sem repasse para notificações de celular.
- Enquanto a chamada abre, nenhum dos lados desiste no primeiro erro: o convidado espera enquanto o convite valer e o anfitrião tenta por 45 s depois de ter a resposta.

### 3.2 Mídia e canal de dados (`cliente/chamada`, `cliente/tela`)

- DTLS-SRTP obrigatório (padrão do WebRTC): só os dois computadores têm as chaves.
- STUN público usado só para descobrir o endereço de cada lado; nenhum dado da chamada passa por ele.
- Retransmissão (TURN) opcional, configurada pela pessoa para redes que não deixam a ligação direta passar: só repassa pacotes já cifrados (DTLS-SRTP). Endereço validado (`turn:`/`turns:`), usuário e senha guardados com DPAPI em `conexao.dat`, nunca enviados pela chamada.
- Canal de dados: JSON de até 16 KB por mensagem, tipos conhecidos, limite de mensagens por segundo; o que não for reconhecido é descartado.
- O app não abre portas de entrada nem fica escutando conexões fora de uma chamada.
- Renegociação de mídia só é aceita pelo canal de sinal e com o mesmo certificado DTLS do início da chamada.

### 3.3 Motores de tradução (`cliente/traducao`)

- Região e chave configuradas só no computador que traduz, cifradas com DPAPI (usuário atual + entropia própria) em `motores.dat`; a chave não volta a ser exibida na tela.
- Nunca enviadas pela chamada, nunca em log; erros do serviço viram frases sem detalhes internos.
- Áudio enviado ao motor só durante a chamada, a partir da captura do próprio app (blocos de até 64 KB).
- Legendas recebidas são aceitas só de quem foi escolhido para traduzir e passam pela mesma validação das mensagens do canal.
- Chat escrito e correções: mensagens de tipo próprio (texto com id `e-…`, correção de um trecho), com texto exibível de até 2.000 caracteres e ao menos uma letra ou número. Quem traduz aceita no máximo 40 por minuto da outra pessoa (cada uma custa uma tradução) e só corrige trechos que são dela — ninguém muda a fala dos outros.
- O motor simulado e a variável que o liga existem só no build Debug.
- Motor local: modelos baixados por HTTPS de versões fixas dos repositórios, conferidos por tamanho e SHA-256 ao baixar e de novo ao carregar (a pasta pode estar em outro disco); arquivo diferente é recusado. A pasta só pode ficar num disco fixo deste computador, e apagar os modelos remove só os arquivos do catálogo.

### 3.4 Dados locais

- Perfil e preferências em `%LOCALAPPDATA%\DinoTranslator`.
- Histórico de chat e falas cifrado com DPAPI; opção de não guardar e botão para apagar.

## 4. Controles comuns aos dois modos

### 4.1 App (`cliente/*`)

- O BlazorWebView só carrega conteúdo local empacotado no app.
- Toda navegação fora da origem do app é cancelada; novas janelas e downloads são bloqueados; links externos abrem no navegador padrão após confirmação.
- CSP estrita: sem scripts, estilos ou fontes de fora do app.
- Câmera, microfone e tela são liberados só para a origem do app; qualquer outra permissão é negada.
- DevTools, menu de contexto padrão, atalhos do navegador e preenchimento automático desligados na versão final.
- Na versão final, o app apaga ao iniciar as variáveis de ambiente do WebView2 (`WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS` e afins), que permitiriam a um atalho ou lançador malicioso ligar a depuração remota, trocar o navegador embutido ou a pasta de dados.
- Variáveis de desenvolvimento (`DINOTRANSLATOR_DATA`, `DINOTRANSLATOR_MODE`, `DINOTRANSLATOR_SERVER`, `DINOTRANSLATOR_RENDEZVOUS`) só existem no build Debug.
- Texto vindo do outro usuário é sempre renderizado como texto.

### 4.2 Repositório e distribuição

- Nada de segredos, bancos de dados, certificados ou builds no Git (`.gitignore`).
- Auditoria de dependências vulneráveis em todo build.
- Instalador assinado; SHA-256 publicado com cada versão.
- Instalação por usuário, sem administrador. O registro em "Aplicativos instalados" é gravado antes dos atalhos e aponta para o `Desinstalar.exe` da pasta do app. O app só conserta o registro numa pasta instalada pelo instalador (registrada, ou apontada pelo atalho do menu Iniciar), e desinstalar uma cópia só remove o registro e os atalhos dela. Os testes usam registro e atalhos próprios.

## 5. Modo servidor (opcional, desligado)

Controles do `DinoTranslator.Server`, mantidos para quando o modo servidor for reativado.

### 5.1 Autenticação (`servidor/autenticacao`)

- Contas criadas **apenas** pelo comando `users add`, executado no próprio servidor. Não existe endpoint de cadastro.
- `MaxUsers = 2` conferido no banco antes de criar qualquer conta.
- Usuário: 3–32 caracteres `[a-z0-9._-]`, comparado em minúsculas.
- Senha: 12 a 128 caracteres; não pode conter o nome de usuário.
- Hash: `PasswordHasher` do ASP.NET Core Identity (PBKDF2-HMAC-SHA512, 100 mil iterações, sal aleatório), com re-hash automático se o formato evoluir.
- Bloqueio: 5 falhas seguidas → 15 min; o contador zera no sucesso.
- Tempo de resposta uniforme: usuário inexistente também passa pelo hash, para não revelar quais usuários existem.
- Limite de requisições: login 10/min por IP; renovação de token 30/min por IP.
- JWT: HS256, chave de no mínimo 256 bits vinda de configuração segura; valida emissor, audiência e expiração (15 min, tolerância de 30 s); aceito só no cabeçalho `Authorization`.
- Refresh token: 256 bits aleatórios, guardado como SHA-256, validade de 30 dias, trocado a cada uso. Reapresentar um token já trocado revoga a sessão inteira.
- Sair revoga a sessão; trocar a senha revoga todas as sessões.
- No app: refresh token cifrado com DPAPI; token de acesso só em memória; validação de certificado padrão do Windows, nunca desligada.

### 5.2 Tempo real (`servidor/sinalizacao`)

- SignalR só aceita usuários autenticados; uma conexão ativa por conta.
- Tamanho máximo de mensagem (64 KB) e validação de tipo e tamanho dos sinais.
- Mensagens só são repassadas ao outro usuário — não existem salas arbitrárias.
- Limite de mensagens por segundo por conexão.

### 5.3 TURN

- `use-auth-secret` com segredo próprio; credenciais válidas por 12 h emitidas pelo servidor.
- Sem acesso a redes internas: `denied-peer-ip` para `0.0.0.0/8`, `10.0.0.0/8`, `100.64.0.0/10`, `127.0.0.0/8`, `169.254.0.0/16`, `172.16.0.0/12`, `192.168.0.0/16`, `::1`, `fc00::/7`, `fe80::/10`; `no-multicast-peers`.
- `no-cli`, `fingerprint`, TLS com o certificado do domínio, cotas por usuário e banda máxima.

### 5.4 Infraestrutura

- Firewall negando tudo exceto 443/TCP (HTTPS), 3478 UDP/TCP e 5349/TCP (TURN), faixa de portas de relay do TURN e SSH.
- SSH só por chave, sem login de root.
- Atualizações automáticas de segurança do sistema.
- Contêineres sem root, imagens com versão fixa, sistema de arquivos somente leitura onde possível.
- Segredos em variáveis de ambiente/arquivos de segredo do Docker com permissão restrita.
- Cabeçalhos: HSTS, `X-Content-Type-Options`, `Referrer-Policy`, `X-Frame-Options`.
