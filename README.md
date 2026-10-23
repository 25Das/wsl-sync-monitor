# WSL Sync Monitor

Monitor de bandeja para Windows que mostra o estado do **WSL** e do **Syncthing rodando dentro do WSL**,
gerencia as distros e protege a sincronização: o WSL só desliga (por ociosidade, pelo app ou junto com o
Windows) depois que o Syncthing terminou de sincronizar.

## Instalação

1. Instale o [.NET 8 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/8.0).
2. Baixe o `WslSyncMonitor.exe` da [última release](../../releases/latest) e abra.
3. Ele oferece se instalar: copia o `.exe` para `%LOCALAPPDATA%\Programs\WslSyncMonitor`, cria atalhos na área de
   trabalho e no menu Iniciar, liga o início com o Windows (cada item pode ser desmarcado) e aparece em
   *Configurações → Aplicativos instalados*, de onde pode ser desinstalado (ou por *Preferências → Desinstalar…*).
   Recusando, ele roda direto de onde está (*Não perguntar de novo* desliga a oferta; *Preferências → Instalar…*
   instala depois).

**Instalações anteriores:** abrir um `.exe` mais novo que o instalado oferece *Atualizar a instalação*; da mesma
versão, *Reinstalar*; mais antigo, não instala por cima e oferece abrir a versão instalada. Se o app já estiver
em execução, ele é fechado com a saída segura (esperando a sincronização) antes da troca. Versões anteriores à
1.9 não atendem a esse pedido: feche-as pelo menu (*Sair*) e abra o instalador de novo.

O `.exe` não é assinado; o Windows SmartScreen pode pedir confirmação (*Mais informações → Executar assim mesmo*)
na primeira instalação.

**Atualizações:** o app procura versão nova ao abrir (e tenta de novo 5 min depois se a rede ainda não
estiver pronta) e uma vez por dia; só essa busca automática mostra notificação, e só com o painel e o menu
fechados. Também dá para buscar à mão, e o resultado aparece onde você buscou:
- **pelo painel** (⟳ ou a versão no cabeçalho): a versão vira o botão *Atualizar para X*;
- **pelo menu** (*Preferências → Verificar atualizações*): o menu continua aberto, o item mostra
  *Verificando atualizações…* e, havendo versão nova, vira *Atualizar para a versão X*.

Com um clique, baixa, troca o `.exe` e reinicia, sem soltar a distro que estiver segurando. O arquivo baixado pelo próprio app não passa pelo SmartScreen. A pasta
do `.exe` precisa permitir escrita (não use `Arquivos de Programas`).

Requisitos: Windows 10/11, WSL 2 e Syncthing instalado na distro (por exemplo como serviço do systemd do usuário).

## O que ele mostra

**Ícone da bandeja:** a cor indica o estado geral e o símbolo diz o que está acontecendo:

| Símbolo | Estado |
|---|---|
| ⏻ cinza | WSL desligado |
| ⟳ azul | sincronizando ou verificando |
| ✓ verde | tudo atualizado |
| ! amarelo | atenção (pasta fora de sincronia, nenhum dispositivo conectado) |
| ✕ vermelho | erro (Syncthing parado, chave recusada, WSL ausente) |

Marcas nos cantos: **azul em cima** = nova versão disponível; **clara embaixo** = distro mantida ligada pelo app.
Passar o mouse mostra a distro e, abaixo, *Pastas: 1 de 1 atualizadas*, *Dispositivos: 2 de 3 conectados*,
*Nova versão* (quando houver) e, por último, até quando a distro fica ligada.

**Widget sobre a barra de tarefas:** `● WSL ● Sync` / `📁 3/3  💻 2/3` (pastas atualizadas / dispositivos
conectados). Acompanha a barra que se oculta; arraste para mudar de lugar. Se não houver espaço
junto da bandeja (ícones demais na barra), ele vai para o primeiro espaço livre do outro lado, sem ocupar o canto
do botão Widgets (clima) do Windows 11. O widget fica **dentro** da barra (é uma janela filha dela): continua
visível com o menu Iniciar aberto e, com a barra que se oculta sozinha, desliza junto com ela. Se der problema no
seu Windows, `WidgetInsideTaskbar: false` volta ao modo antigo (uma janela por cima da barra).

**Atividade em tempo real:** um sync pequeno dura uma fração de segundo. Painel, widget e ícone acompanham o
fluxo de eventos do Syncthing e mostram na hora *Sincronizando relatorio.md…* e, por alguns segundos depois,
*Sincronizado agora · 5 arquivos* (na pasta) e *Sincronizado agora* (no dispositivo que enviou ou recebeu).
Pausar, retomar, erros de pasta e conexões aparecem na hora; *Ver atividade recente…* (menu Syncthing ou de cada
pasta) lista os arquivos recebidos, apagados, alterados aqui e os que falharam.

**Verificação de pastas e dispositivos:** ao iniciar, assim que o Syncthing responde e teve tempo de conectar,
o app procura pastas com erro, fora de sincronia ou pausadas e dispositivos pausados, que nunca conectaram ou
que não conectam há mais de 7 dias (`DeviceStaleDays`), e avisa numa notificação só se encontrar algo (com o
painel e o menu fechados; clicar nela abre o painel). Considera só o que aparece no painel. Também em
*Syncthing ▸ Verificar pastas e dispositivos…*.

**Painel** (clique no ícone ou no widget):
- **Cabeçalho, linha 1:** versão do app, ⓘ ficha técnica e 📌 fixar (o painel fica aberto até ser liberado).
  A versão vira o botão *Atualizar para X* quando há versão nova; clicar nela quando está em dia verifica de novo.
- **Cabeçalho, linha 2:** o estado geral (ou a operação em andamento), *Verificado às HH:mm:ss* e ⟳, que
  verifica o WSL, o Syncthing e se há versão nova do app (F5).
- **WSL:** as distros; a que tem o Syncthing aparece com "· Syncthing".
- **Proteção da sincronização:** o que a proteção está fazendo, sempre começando pelo nome da distro.
- **Syncthing, Pastas e Dispositivos:** estado, tamanho, velocidade, conexões.
- **Rodapé:** com a distro desligada, *Iniciar &lt;distro&gt;*; ligada, *Abrir interface do Syncthing* e
  *Encerrar &lt;distro&gt;*.
- **Toda linha com ▾ abre as ações daquele item** (as mesmas do menu).

## Menu (botão direito)

```
Abrir interface do Syncthing
Atualizar para a versão X                (quando houver)
WSL ▸           cada distro ▸  Abrir terminal · Abrir no Explorer · Reiniciar… · Encerrar… / Iniciar
                               Manter ligada ▸ (até eu desligar · por 1 hora · por 4 horas)
                               Usar para o Syncthing · Definir como padrão do WSL · Exportar backup…
                Encerrar todo o WSL… · Atualizar o WSL… · Editar .wslconfig…
Syncthing ▸     Pausar tudo / Retomar tudo · Verificar todas as pastas agora · Reiniciar o Syncthing…
                Verificar pastas e dispositivos… · Copiar o ID deste dispositivo · Ver atividade recente…
                Ver erros recentes…
                Esperar a sincronização antes de desligar · Ligar o WSL a cada 60 min para sincronizar
Pastas ▸        cada pasta ▸  Abrir no Explorer · Abrir terminal aqui · Verificar agora · Pausar/Retomar
                              Atividade recente… · Arquivos em conflito… · Versões antigas · Copiar caminho
                              Mostrar no painel
                Ocultar pastas não compartilhadas · Ocultar pastas pausadas
Dispositivos ▸  cada dispositivo ▸  Pausar/Retomar · Copiar ID · Pastas compartilhadas… · Mostrar no painel
Diagnóstico ▸   informações rápidas (versão, proteção, chave da API, memória, espaço livre real, último erro)
                Ficha técnica completa… · Copiar ficha técnica
Preferências ▸  Mostrar widget · Restaurar posição do widget · Abrir o painel ao passar o mouse no widget
                Iniciar com o Windows · Verificar atualizações · Configurações avançadas (settings.json)…
                Instalar… / Desinstalar…
Sair
```

Operações que encerram, reiniciam ou alteram algo pedem confirmação.

## Manter ligada

Com só o app aberto, a distro é solta depois de sincronizar e o WSL desliga por ociosidade. Para usar a
distro sem nenhum programa aberto nela (por exemplo, serviços em segundo plano), use **WSL ▸ distro ▸ Manter
ligada**: *até eu desligar* (vale também depois de reiniciar o app, que liga a distro ao abrir se ela estiver
desligada), *por 1 hora* ou *por 4 horas*. Ao acabar,
o app espera a sincronização e solta a distro normalmente. Encerrar a distro cancela o pedido.

## Proteção da sincronização

Com **Esperar a sincronização antes de desligar** (menu *Syncthing*, ligado por padrão), o app mantém a distro monitorada ligada
enquanto ela estiver em uso e, quando o último programa (VS Code, terminal, dev container) fecha, só a solta
depois de confirmar que o Syncthing terminou. Então o desligamento por ociosidade do WSL age normalmente.
O mesmo vale ao sair do app e ao desligar o Windows. Com **Ligar o WSL a cada 60 min para sincronizar**, o app liga o WSL (se estiver desligado) só
para sincronizar.

O estado aparece no painel, na seção *Proteção da sincronização*. Como funciona:

- **Em uso:** enquanto há sessões de outros programas na distro (processos `wsl.exe` do Windows que apontam
  para ela: VS Code, dev containers, terminais), o app mantém a distro ligada.
- **Quando a última sessão fecha:** o app pede ao Syncthing para verificar todas as pastas na hora, espera as
  pastas compartilhadas ficarem paradas e sem pendências e os dispositivos **conectados** chegarem a 100%, exige
  que isso dure alguns segundos e só então solta a distro. Se já estava tudo sincronizado, leva ~10–15 s.
- **Iniciar pelo app / sincronização periódica:** liga, sincroniza, verifica e solta.
- **Sair do app:** se a distro está ligada só por causa do app, ele espera a sincronização antes de fechar
  (clicar em *Sair* de novo fecha sem esperar).
- **Desligar o Windows:** o app segura o desligamento com o motivo *"Aguardando o Syncthing terminar…"*,
  espera a sincronização, para o Syncthing pela API, grava os dados em disco e encerra a distro. O
  *Desligar mesmo assim* do Windows continua disponível.
- Há limites para todas as esperas (veja *Configurações*); passado o limite, o app solta/encerra mesmo assim.

## Encerrar (modo seguro)

Encerrar ou reiniciar uma distro (ou todo o WSL, inclusive antes de *Atualizar o WSL*) pede confirmação e então:
1. para o Syncthing de forma limpa: pela API (`/rest/system/shutdown`) na distro monitorada, ou com `SIGTERM` nas demais; espera o processo sair (até 30 s);
2. executa `sync` para gravar os dados em disco;
3. executa `wsl --terminate <distro>` (ou `wsl --shutdown` para todo o WSL).

Se o Syncthing não encerrar, nada é encerrado e um aviso é mostrado.

O app não trava o próprio `.exe`: o processo que mantém a distro ligada é criado sem herdar handles, então o executável pode ser substituído com o app fechado mesmo com o WSL ligado.

## Configurações

Botão direito → *Editar configurações…* abre `%APPDATA%\WslSyncMonitor\settings.json`
(as mudanças são aplicadas automaticamente ao salvar):

| Chave | Padrão | Descrição |
|---|---|---|
| `Distro` | `""` | Distro onde o Syncthing roda (vazio = padrão do WSL). Também pelo menu da distro → *Monitorar o Syncthing desta distro*. |
| `SyncthingUrl` | `""` | Força a URL da GUI (ex.: `http://127.0.0.1:8384`). |
| `ApiKey` | `""` | Força a chave da API (Syncthing → Ações → Configurações → Geral). |
| `SyncthingConfigPath` | `""` | Caminho do `config.xml` dentro do WSL, se não estiver no local padrão. |
| `RefreshSeconds` | `5` | Intervalo de atualização. |
| `ShowTaskbarWidget` | `true` | Mostra o mini‑widget sobre a barra. |
| `WidgetOffset` | `0` | Distância extra do widget até a bandeja. **Arraste o widget** para ajustar. |
| `HideUnsharedFolders` | `true` | Oculta pastas não compartilhadas com nenhum dispositivo. |
| `HidePausedFolders` | `false` | Oculta pastas pausadas. |
| `HiddenFolders` | `[]` | IDs das pastas ocultadas manualmente. |
| `HoverPopup` | `false` | Abre os detalhes ao passar o mouse sobre o widget (o ícone da bandeja abre só com clique). |
| `ProtectSync` | `true` | Segura o WSL até o Syncthing sincronizar. |
| `MaxHoldMinutes` | `10` | Limite da espera (e ao sair do app). |
| `ShutdownMaxWaitSeconds` | `120` | Limite da espera ao desligar o Windows. |
| `StartupGraceSeconds` | `90` | Tempo para o Syncthing conectar após ligar. |
| `StableSeconds` | `10` | Tempo que "sincronizado" precisa durar. |
| `PeriodicSyncMinutes` | `60` | Sincronização periódica (0 = desligada). |
| `WidgetInsideTaskbar` | `true` | Widget dentro da barra de tarefas (`false` = por cima dela, modo antigo). |
| `DeviceStaleDays` | `7` | Verificação de pastas e dispositivos: avisa de dispositivo que não conecta há tantos dias. |
| `KeepAliveDistro` | `""` | Distro mantida ligada "até eu desligar". |
| `HiddenDevices` | `[]` | IDs dos dispositivos ocultados do painel. |
| `HoverDelayMs` | `450` | Atraso do hover. |

## Problemas comuns

Para qualquer problema, use **Diagnóstico ▸ Ficha técnica completa…** (ou o ⓘ do painel): versão do app e do WSL, distro monitorada,
estado da proteção, URL usada, de onde veio a chave da API (a chave em si nunca é mostrada), quais `config.xml`
foram encontrados e o erro do último script executado na distro. O botão *Copiar* copia tudo para enviar.

**Log:** *Diagnóstico ▸ Abrir a pasta de logs* abre `%APPDATA%\WslSyncMonitor\logs`, com um arquivo por dia
(`app-AAAAMMDD.log`, mantidos por 14 dias). Ficam registrados o início e a saída do app, o que a proteção faz
(segurar, verificar, liberar a distro), cada passo do desligamento do Windows, falhas do `wsl.exe` (com o código
explicado), da API do Syncthing e da busca de atualizações, e erros das ações. Falhas que se repetem a cada
atualização entram uma vez, e de novo quando voltam ao normal. A chave da API nunca é gravada. O final do log
aparece na ficha técnica.

- **"Sem resposta da API"**: confirme que `localhostForwarding` não está desativado no `.wslconfig`
  ou use `networkingMode=mirrored`; ou defina `SyncthingUrl`.
- **"Chave da API inválida"**: o painel mostra de onde a chave foi lida. Se o Syncthing usa outro arquivo de
  configuração ou roda com outro usuário (ex.: serviço como root), defina `SyncthingConfigPath` ou `ApiKey`.
- **Widget sobre os ícones da barra** (Windows 11 com ícones à esquerda, etc.): arraste o widget para outro ponto
  ou desative em *Widget na barra de tarefas* e use só o ícone da bandeja.
- Apenas a barra de tarefas do monitor principal é acompanhada.
- **"wsl.exe – Erro de aplicativo (0xc0000142)" ao desligar o Windows** (até a 1.11.2): depois do aviso de
  desligamento o Windows não deixa iniciar nenhum processo, e a janela de erro **segurava o desligamento** até
  ser fechada. Desde a 1.11.3 o app não inicia nenhum `wsl.exe` depois do aviso: espera a sincronização e para o
  Syncthing pela API; para gravar os dados em disco (`sync`), cria um arquivo-sinal em
  `\\wsl.localhost\<distro>\tmp\wsl-sync-monitor` que o processo de manutenção (o que já mantém a distro
  ligada) atende. A distro é encerrada pelo próprio Windows. O log mostra cada passo.
- **O painel mostra "em uso" sem nenhum programa aberto** (ou o contrário): a detecção usa os processos
  `wsl.exe` do Windows; algum programa pode estar mantendo uma sessão aberta na distro.

## Como ele obtém os dados

- `wsl.exe --list --verbose` e `--list --running --quiet` (não liga o WSL; funciona em qualquer idioma).
- Com a distro em uso, lê o `config.xml` do Syncthing (`~/.local/state/syncthing/`, `~/.config/syncthing/` ou
  snap; outro local pela configuração `SyncthingConfigPath`) para descobrir a chave da API e a porta da GUI, e
  consulta a REST API em `http://127.0.0.1:<porta>` (encaminhamento de localhost do WSL 2). Se localhost não
  responder, tenta o IP da VM. Depois que a distro é liberada para desligar, nada é executado dentro dela.
- O estado completo é lido periodicamente; entre uma leitura e outra, o fluxo de eventos do Syncthing
  (`/rest/events`, um pedido que fica aberto até haver novidade) traz na hora mudanças de estado das pastas,
  arquivos sincronizados e conexões de dispositivos. A espera pela sincronização (ao liberar a distro, ao sair e
  ao desligar) também reage a esses eventos em vez de conferir a cada 2 s. Mudanças de configuração (pasta ou
  dispositivo novo, removido, pedido de conexão) disparam uma leitura completa na hora.
- Com o fluxo de eventos conectado e o painel fechado, a leitura completa fica 3 vezes mais espaçada (no mínimo
  15 s), a não ser que a proteção dependa dela; uma sessão do WSL que fecha (VS Code, terminal) avisa na hora.
- Logo depois de a distro ligar (ou de o Syncthing parar), o app confere `/rest/noauth/health` a cada segundo (sem
  chave e sem executar nada na distro) e lê tudo assim que o Syncthing responde.
- O widget acompanha a barra pelos avisos que a própria barra usa: janelas com botão nela (shell hook), troca de
  área de trabalho virtual (cloak), tela cheia, posição e modo de ocultar (registrado como appbar, sem reservar
  espaço), Explorer reiniciado (TaskbarCreated), além de tema e monitores. O painel aberto pela passagem do mouse
  fecha pelos avisos de saída do mouse, sem conferir de tempos em tempos.
- Distro encerrada por fora é percebida na hora (o processo que a mantém ligada avisa quando termina); instalar,
  remover ou trocar a distro padrão também (o Windows avisa quando a lista de distros muda no registro; os dados
  continuam vindo do `wsl.exe`).
- Ao desligar ou sair, o fim do Syncthing é percebido pelo fechamento da conexão aberta do fluxo de eventos. Para
  esperar um processo sair da distro, o app usa um único `wsl.exe` com o `waitpid` do util-linux 2.40+ (avisado
  pelo kernel); sem ele, confere de dentro da distro.

## Roadmap

Ideias para o futuro (ainda não implementadas):
- **Independente de sistema operacional:** rodar também fora do Windows (hoje o app é WinForms e depende do WSL).
- **Independente da estrutura das conexões remotas:** não supor uma topologia fixa de dispositivos (como o
  triângulo trabalho · casa · VM); funcionar com qualquer arranjo de dispositivos e pastas.
- **Independente das configurações do WSL do usuário:** hoje o app foi pensado para quem deixa o WSL desligar
  sozinho por ociosidade (decisão pessoal do autor); o essencial é a proteção da sincronização, que deve valer
  com qualquer configuração (`vmIdleTimeout`, rede, systemd etc.).
- **Mapear outras regras muito específicas de um usuário:** identificar comportamentos que hoje supõem o
  ambiente do autor e torná-los opcionais ou configuráveis, com a mesma intenção acima.
- **Idiomas:** a interface é só em português; adaptar para outros idiomas.

## Compilar

Requer o [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). No Windows, `build.cmd` gera
`dist\WslSyncMonitor.exe`. **Fluxo de trabalho:** cada mudança vai para um branch; o GitHub Actions roda os testes no Linux (`tests/`) e no
Windows (`tests.windows/`, código de interface) e compila; só com tudo verde ela entra na `main`, de onde sai a
release. Um hook de pre-commit (`git config core.hooksPath .githooks`, uma vez por clone) roda os testes do Linux
e a compilação antes de cada commit.

O projeto segue TDD: toda correção ou funcionalidade começa por um teste que falha. Os testes ficam
em `tests/` (xUnit) e cobrem o que não depende do Windows, como os scripts de shell executados dentro da
distro, que rodam com o `sh` do Linux: `dotnet test tests`. O GitHub Actions roda os testes e depois compila
a cada push; sem testes passando não há build nem release. Para publicar uma versão, altere `<Version>` em
`src/WslSyncMonitor.csproj` e faça push na `main`: o workflow cria a tag `v<versão>` e a release com o
`WslSyncMonitor.exe` anexado.

**Ferramentas de qualidade:**
- **Analisadores do .NET** no nível recomendado (`Directory.Build.props`; regras que não se aplicam estão
  desligadas e explicadas no `.editorconfig`). A compilação do pre-commit trata alertas como erro.
- **Capturas de tela e cobertura:** a cada push, o job do Windows desenha o painel, o widget e o ícone em vários
  estados (temas claro e escuro) e mede a cobertura dos testes; tudo vai para o branch
  [`ci-evidencias`](../../tree/ci-evidencias), numa pasta com o nome do branch (`index.md` com as imagens e
  `cobertura.md`). Dá para conferir o visual sem um Windows.
- **Testes de ponta a ponta** (`tests.windows/E2E`, FlaUI): no Windows do CI, o app de verdade é aberto e usado
  com mouse e teclado (oferta de instalação, widget, menu, busca de atualização pelo menu, painel, Sair), com um
  servidor falso de atualizações e uma pasta de dados temporária (`WSLSYNCMONITOR_UPDATE_URL`,
  `WSLSYNCMONITOR_HOME`). Cada passo deixa uma captura da tela real (`e2e-*.png` em `ci-evidencias`). Só rodam
  com `WSM_E2E=1`, para não atrapalhar quem roda os testes no próprio PC.
- **Testes de regras também no Windows:** os testes de `tests/` rodam nas duas plataformas (os que executam o
  `sh` do Linux, marcados com `[ShFact]`, só no Linux).
- **Testes com o Syncthing de verdade** (`[SyncthingFact]`): com o `syncthing` no PATH (no CI do Linux ele é
  instalado e esses testes nunca são pulados), cada teste sobe uma instância temporária em portas livres e confere
  o fluxo de eventos de ponta a ponta.
