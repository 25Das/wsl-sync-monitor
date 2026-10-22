# WSL Sync Monitor

Monitor de bandeja para Windows que mostra o estado do **WSL** e do **Syncthing rodando dentro do WSL**,
gerencia as distros e protege a sincronização: o WSL só desliga (por ociosidade, pelo app ou junto com o
Windows) depois que o Syncthing terminou de sincronizar.

## Instalação

1. Instale o [.NET 8 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/8.0).
2. Baixe o `WslSyncMonitor.exe` da [última release](../../releases/latest) e coloque numa pasta fixa.
3. Abra. Para iniciar com o Windows: botão direito → *Iniciar com o Windows*.

O `.exe` não é assinado; o Windows SmartScreen pode pedir confirmação (*Mais informações → Executar assim mesmo*).

Requisitos: Windows 10/11, WSL 2 e Syncthing instalado na distro (por exemplo como serviço do systemd do usuário).

## O que ele mostra

**Na barra de tarefas (resumido)**
- **Ícone na bandeja**: círculo colorido com o estado geral e o número de dispositivos conectados
  (`W` = WSL parado, `!` = erro/Syncthing fora do ar).
- **Mini‑widget sobre a barra** (ao lado da área de notificação; a largura se ajusta ao conteúdo):
  `● WSL  ● Sync` / `📁 3/3  💻 2/3` (pastas atualizadas / dispositivos conectados).
  Em barras pequenas vira uma linha só; em barras verticais, um bloco compacto.

**Ao passar o mouse ou clicar (detalhado)**
- Distros do WSL (padrão, versão WSL1/2, em execução/parada).
- Syncthing: versão, tempo ativo, ID local, taxa ↓/↑, URL.
- Cada pasta: estado (Atualizada, Sincronizando 42%, Verificando, Fora de sincronia, Erro, Pausada),
  tamanho, nº de dispositivos com quem é compartilhada, caminho e barra de progresso.
- Cada dispositivo remoto: Conectado/Atualizado, Sincronizando %, Desconectado (visto há X), Pausado,
  endereço, tipo de conexão (tcp/quic/relay), versão e taxa ↓/↑.
- Botões **Abrir Syncthing** (abre a interface web no navegador padrão; só aparece com o Syncthing rodando), **Iniciar WSL / Encerrar WSL** e **Atualizar**.
  Ao iniciar ou encerrar, o andamento aparece no cabeçalho ("iniciando WSL…", "aguardando Syncthing…") e o ponto do WSL no widget fica azul.

**Ícone da bandeja**: clique esquerdo abre/fecha o painel; clique direito abre o menu; duplo clique abre o Syncthing.
**Widget**: clique abre/fecha o painel; clique direito abre o menu. Opcional (menu → *Detalhes ao passar o mouse no widget*, desligado por padrão): passar o mouse mostra o painel.
O painel fixado fecha com Esc ou clicando fora.

## Proteção da sincronização

Com **Segurar o WSL até sincronizar** (menu, ligado por padrão), o app mantém a distro monitorada ligada
enquanto ela estiver em uso e, quando o último programa (VS Code, terminal, dev container) fecha, só a solta
depois de confirmar que o Syncthing terminou. Então o desligamento por ociosidade do WSL age normalmente.
O mesmo vale ao sair do app e ao desligar o Windows. Com **Sincronização periódica**, o app liga o WSL a cada
60 min (se estiver desligado) só para sincronizar.

O estado aparece no painel, na seção WSL ("Proteção: …"). Como funciona:

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

## Distros

O menu (botão direito) lista todas as distros, com um ponto verde para as que estão em execução. Cada uma tem:
- **Abrir terminal**;
- **Iniciar** / **Encerrar…**;
- **Monitorar o Syncthing desta distro** (a monitorada aparece com "· Syncthing" no nome);
- **Definir como padrão do WSL**.

As mesmas ações aparecem ao clicar numa distro no painel detalhado. **Encerrar todo o WSL…** encerra todas as distros e a máquina virtual (`wsl --shutdown`).

## Pastas exibidas

Menu → *Pastas exibidas*:
- marque/desmarque cada pasta para mostrá‑la ou ocultá‑la;
- **Ocultar pastas não compartilhadas** (ligado por padrão): esconde pastas sem nenhum dispositivo remoto, como a "Default Folder" que o Syncthing cria e muitas vezes não é usada;
- **Ocultar pastas pausadas** (desligado por padrão).

Pastas ocultas não entram na contagem do widget nem na cor do estado geral.

## Encerrar (modo seguro)

Encerrar uma distro (ou todo o WSL) pede confirmação e então:
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
| `HoverDelayMs` | `450` | Atraso do hover. |

## Problemas comuns

- **"Sem resposta da API"**: confirme que `localhostForwarding` não está desativado no `.wslconfig`
  ou use `networkingMode=mirrored`; ou defina `SyncthingUrl`.
- **"Chave da API inválida"**: o Syncthing roda com outro usuário (ex.: serviço como root) → defina `ApiKey`
  ou `SyncthingConfigPath`.
- **Widget sobre os ícones da barra** (Windows 11 com ícones à esquerda, etc.): arraste o widget para outro ponto
  ou desative em *Widget na barra de tarefas* e use só o ícone da bandeja.
- Apenas a barra de tarefas do monitor principal é acompanhada.
- **O painel mostra "em uso" sem nenhum programa aberto** (ou o contrário): a detecção usa os processos
  `wsl.exe` do Windows; algum programa pode estar mantendo uma sessão aberta na distro.

## Como ele obtém os dados

- `wsl.exe --list --verbose` e `--list --running --quiet` (não liga o WSL; funciona em qualquer idioma).
- Com a distro ligada, lê o `config.xml` do Syncthing (`~/.local/state/syncthing/`, `~/.config/syncthing/` ou
  snap) para descobrir a chave da API e a porta da GUI, e consulta a REST API em `http://127.0.0.1:<porta>`
  (encaminhamento de localhost do WSL 2). Se localhost não responder, tenta o IP da VM.

## Compilar

Requer o [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). No Windows, `build.cmd` gera
`dist\WslSyncMonitor.exe`. As releases são geradas pelo GitHub Actions ao enviar uma tag `v*`
(por exemplo `git tag v1.5.0 && git push origin v1.5.0`).
