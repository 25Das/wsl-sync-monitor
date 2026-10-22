# WSL Sync Monitor

Monitor de bandeja para Windows que mostra o estado do **WSL** e do **Syncthing rodando dentro do WSL**,
gerencia as distros e protege a sincronização: o WSL só desliga (por ociosidade, pelo app ou junto com o
Windows) depois que o Syncthing terminou de sincronizar.

## Instalação

1. Instale o [.NET 8 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/8.0).
2. Baixe o `WslSyncMonitor.exe` da [última release](../../releases/latest) e coloque numa pasta fixa.
3. Abra. Para iniciar com o Windows: botão direito → *Iniciar com o Windows*.

O `.exe` não é assinado; o Windows SmartScreen pode pedir confirmação (*Mais informações → Executar assim mesmo*)
na primeira instalação.

**Atualizações:** o app verifica a última release ao abrir e uma vez por dia (ou em *menu → Verificar
atualizações*). Quando há versão nova, avisa na bandeja e mostra *Atualizar para a versão X* no menu; com um
clique, baixa, troca o `.exe` e reinicia, sem soltar a distro que estiver segurando. O arquivo baixado pelo
próprio app não passa pelo SmartScreen. A pasta do `.exe` precisa permitir escrita (não use `Arquivos de Programas`).

Requisitos: Windows 10/11, WSL 2 e Syncthing instalado na distro (por exemplo como serviço do systemd do usuário).

## O que ele mostra

**Ícone da bandeja:** a cor indica o estado geral e o símbolo diz o que está acontecendo:

| Símbolo | Estado |
|---|---|
| ⏻ cinza | WSL desligado |
| ⟳ azul | sincronizando ou verificando |
| ✓ verde | tudo atualizado |
| ! amarelo | atenção (dispositivo desconectado, pasta fora de sincronia) |
| ✕ vermelho | erro (Syncthing parado, chave recusada, WSL ausente) |

Marcas nos cantos: **azul em cima** = nova versão disponível; **clara embaixo** = distro mantida ligada pelo app.
Passar o mouse mostra um resumo (pastas atualizadas, dispositivos conectados, até quando a distro fica ligada).

**Widget sobre a barra de tarefas:** `● WSL ● Sync` / `📁 3/3  💻 2/3` (pastas atualizadas / dispositivos
conectados). Acompanha a barra que se oculta; arraste para mudar de lugar.

**Painel** (clique no ícone ou no widget):
- **Cabeçalho:** ⟳ recarregar (F5), ⓘ ficha técnica e 📌 fixar (o painel fica aberto até ser liberado).
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
                Copiar o ID deste dispositivo · Ver erros recentes…
                Esperar a sincronização antes de desligar · Ligar o WSL a cada 60 min para sincronizar
Pastas ▸        cada pasta ▸  Abrir no Explorer · Abrir terminal aqui · Verificar agora · Pausar/Retomar
                              Arquivos em conflito… · Versões antigas · Copiar caminho · Mostrar no painel
                Ocultar pastas não compartilhadas · Ocultar pastas pausadas
Dispositivos ▸  cada dispositivo ▸  Pausar/Retomar · Copiar ID · Pastas compartilhadas… · Mostrar no painel
Diagnóstico ▸   informações rápidas (versão, proteção, chave da API, memória e disco, último erro)
                Ficha técnica completa… · Copiar ficha técnica
Preferências ▸  Mostrar widget · Restaurar posição do widget · Abrir o painel ao passar o mouse no widget
                Iniciar com o Windows · Verificar atualizações · Configurações avançadas (settings.json)…
Sair
```

Operações que encerram, reiniciam ou alteram algo pedem confirmação.

## Manter ligada

Com só o app aberto, a distro é solta depois de sincronizar e o WSL desliga por ociosidade. Para usar a
distro sem nenhum programa aberto nela (por exemplo, serviços em segundo plano), use **WSL ▸ distro ▸ Manter
ligada**: *até eu desligar* (vale também depois de reiniciar o app), *por 1 hora* ou *por 4 horas*. Ao acabar,
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
| `KeepAliveDistro` | `""` | Distro mantida ligada "até eu desligar". |
| `HiddenDevices` | `[]` | IDs dos dispositivos ocultados do painel. |
| `HoverDelayMs` | `450` | Atraso do hover. |

## Problemas comuns

Para qualquer problema, use **Diagnóstico ▸ Ficha técnica completa…** (ou o ⓘ do painel): versão do app e do WSL, distro monitorada,
estado da proteção, URL usada, de onde veio a chave da API (a chave em si nunca é mostrada), quais `config.xml`
foram encontrados e o erro do último script executado na distro. O botão *Copiar* copia tudo para enviar.


- **"Sem resposta da API"**: confirme que `localhostForwarding` não está desativado no `.wslconfig`
  ou use `networkingMode=mirrored`; ou defina `SyncthingUrl`.
- **"Chave da API inválida"**: o painel mostra de onde a chave foi lida. Se o Syncthing usa outro arquivo de
  configuração ou roda com outro usuário (ex.: serviço como root), defina `SyncthingConfigPath` ou `ApiKey`.
- **Widget sobre os ícones da barra** (Windows 11 com ícones à esquerda, etc.): arraste o widget para outro ponto
  ou desative em *Widget na barra de tarefas* e use só o ícone da bandeja.
- Apenas a barra de tarefas do monitor principal é acompanhada.
- **O painel mostra "em uso" sem nenhum programa aberto** (ou o contrário): a detecção usa os processos
  `wsl.exe` do Windows; algum programa pode estar mantendo uma sessão aberta na distro.

## Como ele obtém os dados

- `wsl.exe --list --verbose` e `--list --running --quiet` (não liga o WSL; funciona em qualquer idioma).
- Com a distro em uso, lê o `config.xml` do Syncthing (`~/.local/state/syncthing/`, `~/.config/syncthing/` ou
  snap; outro local pela configuração `SyncthingConfigPath`) para descobrir a chave da API e a porta da GUI, e
  consulta a REST API em `http://127.0.0.1:<porta>` (encaminhamento de localhost do WSL 2). Se localhost não
  responder, tenta o IP da VM. Depois que a distro é liberada para desligar, nada é executado dentro dela.

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
