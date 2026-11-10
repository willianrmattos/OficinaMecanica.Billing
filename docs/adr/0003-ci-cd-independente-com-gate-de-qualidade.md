# ADR 0003: CI/CD independente com gate de qualidade

## Status
Aceito

## Contexto
O Billing e um microsservico independente: repositorio, banco (`BillingDb`), credencial de banco dedicada, identidade
OIDC propria e rota propria na APIM. Ele precisa de um pipeline proprio que prove, a cada PR, que o codigo compila, que
os testes (inclusive os de infraestrutura com SQL Server e RabbitMQ reais) passam, que a cobertura de linhas e de pelo
menos 80% e que a qualidade estatica esta aprovada, e que depois publique a imagem e implante no AKS sem segredo de
longa duracao. Este pipeline e o molde dos outros servicos (Execucao e OS Service), entao as decisoes precisam ser
claras e as partes especificas do servico ficam isoladas em poucos pontos.

Restricoes que moldam as escolhas: o MassTransit 9.x e comercial (o ecossistema fica no 8.x); o cluster tem um unico no
sem CPU de requests sobrando; o Azure SQL e serverless (pausa por inatividade); o repositorio ainda nao existe no GitHub,
entao tudo foi validado localmente (`actionlint`, `kubeconform`, `docker build` e execucao dos scripts) e a primeira
execucao real do workflow acontece na publicacao.

## Decisao
**Cinco jobs encadeados em `.github/workflows/ci.yml`:**
- `build-and-test` (`Build & Test`): restore, build Release, testes dos scripts de CI, guardrail do MassTransit, validacao dos
  manifests, conferencia do OpenAPI, testes `Category!=Infra`, testes `Category=Infra` (Testcontainers; o runner ubuntu ja traz
  Docker), cobertura mesclada com gate e publicacao dos resultados (`dorny/test-reporter`, Job Summary e artifacts).
- `quality` (`Quality`): SonarCloud, depende do job anterior.
- `scan-image` (`Scan Image (Trivy)`): so `push`/`workflow_dispatch` em `main` e `release`; PR nunca publica imagem. Roda com
  `contents: read` SOMENTE: constroi a imagem, escaneia com o Trivy e salva a imagem aprovada como artifact.
- `build-and-push-image`: com `id-token: write`; baixa o artifact, `docker load`, login OIDC e push da MESMA imagem.
- `deploy-to-aks`: mesma condicao; migrations, `kubectl apply`, `kubectl set image`, `rollout status`; com
  `concurrency: deploy-aks-billing` (`cancel-in-progress: false`), porque `main` e `release` implantam no mesmo Deployment.

**Gate de cobertura de 80% sobre o numero mesclado.** `scripts/cobertura.sh` coleta OpenCover (coverlet) e `.trx` em cada
projeto de teste na mesma execucao (os testes rodam uma unica vez) e une tudo com o ReportGenerator, inclusive os testes
de infraestrutura. O gate (`scripts/verificar-cobertura.sh`) vale para o numero "com exclusoes", que tira apenas o que o
`CLAUDE.md` documenta (`Migrations`, `Program.cs`, `DependencyInjection*`, EF `Configurations`); o numero bruto e
publicado ao lado. O limite e `COBERTURA_MINIMA` (padrao 80) e o script falha com codigo de saida diferente de zero abaixo
dele. O percentual sai da contagem de linhas cobertas e cobertaveis, nao da linha "Line coverage" (arredondada para uma casa:
79,95% apareceria como 80% e passaria). A mesma lista de exclusoes alimenta o Sonar (`sonar.coverage.exclusions`).
O atributo `[ExcludeFromCodeCoverage]` e honrado pelo coverlet e some das duas medicoes sem deixar rastro no gate; por isso o
uso e proibido sem justificativa em ADR e o `cobertura.sh` lista as ocorrencias no relatorio (o `testar-scripts.sh` confere que o
aviso lista o arquivo de `src/`, ignora `obj/` e que o resumo conta as ocorrencias).

**Teste preso nao pode travar o pipeline.** Um teste que espera uma tarefa que o codigo sob teste deixou de liberar nao reprova:
fica pendurado, e o run so terminaria no `timeout-minutes` do job (e, no `ci-local.sh`, nunca). Duas defesas: os testes que
esperam tarefa controlada por eles (health checks com `TaskCompletionSource`) limitam a espera com `AguardarAsync` (10 s) e falham com
a explicacao do que deveria ter acontecido; e o `cobertura.sh` roda `dotnet test` com `--blame-hang-timeout 5m --blame-hang-dump-type none`,
que encerra o testhost de qualquer teste parado por 5 minutos sem gravar despejo de memoria em `TestResults`.

**Os scripts de decisao tem testes.** `scripts/testar-scripts.sh` (no `Build & Test` e no `ci-local.sh`) exercita o gate e o
guardrail com entradas sinteticas, inclusive os caminhos de reprovacao (79,9% reprova, 80,0% aprova, numero ausente nao aprova,
`COBERTURA_MINIMA` vazio cai em 80, MassTransit 9.x direto ou transitivo reprova, `MassTransit.EntityFrameworkCore` 9.x reprova),
tambem a separacao de privilegios do proprio `ci.yml` (ver abaixo), e planta defeitos em copias dos scripts (gate que sai 0,
minimo padrao zerado, guardrail que aceita o major 9, verificador do workflow que libera `aquasecurity/*`, entre outros),
exigindo que cada um seja detectado.
Sem isso, o gate e o guardrail so seriam vistos passando num CI saudavel, e um defeito que os tornasse inofensivos nunca apareceria.
Testar o `verificar-cobertura.sh` isolado nao basta: se alguem apagar a chamada do gate em `cobertura.sh`, o CI deixa de
reprovar cobertura e nenhum caso do gate percebe. Por isso o `cobertura.sh relatorio` tambem roda de ponta a ponta sobre um
OpenCover sintetico (79% reprova, 81% aprova, o numero vem "com exclusoes", a variavel `COBERTURA_MINIMA` chega ao gate, fase
invalida e relatorio sem OpenCover saem com 2), o `validar-k8s.sh` roda o kubeconform sobre manifests sinteticos (campo
desconhecido reprova por causa do `-strict`; o CRD sem schema e ignorado), o `exportar-swagger.sh --verificar` roda com um
`dotnet` de mentira que grava um documento sintetico (em dia aprova, divergente ou sem `docs/openapi.json` reprova, argumento
invalido sai com 2), e se confere que `ci.yml` e `ci-local.sh` chamam os mesmos passos e que o `ci.yml` exporta `COBERTURA_MINIMA: "80"` (um mutante troca o 80 por 10 numa
copia do `ci.yml`: reduzir o valor do gate no workflow nao pode passar despercebido). Cada script tem mutantes proprios (a chamada
do gate apagada, o gate lendo o numero bruto, cada filtro de exclusao removido, `-strict` retirado, falha do kubeconform ignorada...).
Os casos que dependem do Docker (kubeconform) sao pulados com aviso quando ele nao esta ligado. Na ultima execucao completa
foram 72 casos e 33 mutantes detectados (o mesmo conjunto de casos do molde da Execucao; os mutantes a mais sao os filtros de
exclusao de cobertura do Billing, um por padrao: Migrations, `Program.cs`, `DependencyInjection*` e EF Configurations).

**SonarCloud com Quality Gate bloqueante.** O job `quality` baixa o OpenCover do job anterior (artifact), roda
`dotnet-sonarscanner begin/build/end` e usa `sonar.qualitygate.wait=true`: se o Quality Gate reprova, o job falha. Projeto e
organizacao vem de variaveis do repositorio (`vars.SONAR_PROJECT_KEY`, `vars.SONAR_ORGANIZATION`) e o `SONAR_TOKEN` de
`secrets`; e o unico secret de longa duracao do pipeline, porque o SonarCloud nao suporta OIDC. Sem token o job falha com
mensagem clara; em PR de fork (sem acesso a secrets) o job e pulado.

**Trivy antes do push, em job sem OIDC.** O job `scan-image` constroi com `load: true` (sem push), escaneia com
`aquasecurity/trivy-action` (falha em `HIGH`/`CRITICAL` com correcao disponivel, `ignore-unfixed`) e salva a imagem aprovada
(`docker save`) como artifact. O job `build-and-push-image` so baixa o artifact, carrega e empurra a MESMA imagem que foi
escaneada. A separacao existe porque o Trivy e as actions do Docker sao de terceiros e o job de push tem `id-token: write`:
qualquer step de um job com essa permissao pode pedir o token OIDC, e a identidade do servico tem `AcrPush`, `AKS Cluster
Admin` e leitura do Key Vault. Com a divisao, o job que executa codigo de terceiros so tem `contents: read` e o que tem OIDC so
executa actions do GitHub/Microsoft. A tag de deploy e sempre o sha do commit; `:latest` so sai da `main`, para um push em
`release` nao sobrescrever o que esta em producao.

**A separacao de privilegios tem guarda automatica.** `scripts/verificar-workflow.sh` le o `ci.yml` (ignorando comentarios) e
reprova se um job que declara `id-token` executar qualquer action fora de `actions/*` e `azure/*`, ou se `id-token`/`write-all`
for concedido no nivel do workflow (valeria para todos os jobs). O `testar-scripts.sh` o roda contra o `ci.yml` real e contra
workflows sinteticos de falha (o job de push com a action do Trivy, com a action entre aspas, `id-token` no `scan-image`,
CRLF) e contra mutantes do proprio verificador. Sem essa guarda, uma action de terceiros colocada de volta no job de push passaria
despercebida: a separacao so existia como comentario e como desenho do `ci.yml`.

**Guardrail do MassTransit.** `scripts/verificar-masstransit.sh` roda `dotnet list package --include-transitive` e falha se
qualquer pacote `MassTransit*`, direto ou transitivo, resolver para major diferente de 8. O Dependabot tambem ignora o
major do `MassTransit*` (e o major do EF Core, que continua no 9.x por causa do `net8.0` e do
`MassTransit.EntityFrameworkCore 8.5.12`).

**OIDC.** `azure/login` com a identidade do Billing (`github_oidc_aks_app` no `OficinaMecanica.Infra`: so `push` em
`main`/`release`, sem credencial para `pull_request`; `AcrPush`, `AKS Cluster Admin` e `Key Vault Secrets User` apenas no
secret `billing-sql-connection-string`). Os jobs que falam com o Azure declaram `id-token: write`; os demais ficam com
`contents: read`. Todo `actions/checkout` usa `persist-credentials: false`.

**Actions por SHA (aplicado em 2026-11-09).** Todas as actions do `ci.yml`, de terceiros (`docker/*`, `aquasecurity/trivy-action`,
`dorny/test-reporter`) e do GitHub/Microsoft (`actions/*`, `azure/*`), estao fixadas pelo SHA completo do commit, com a versao num
comentario ao lado (`uses: actions/checkout@<sha> # v4.4.0`). Uma tag e mutavel: quem controla a action pode move-la para outro
codigo; o SHA nao. O SHA veio da tag que o workflow ja usava, resolvida pela API do GitHub para o commit em que ela apontava no dia,
e a versao do comentario e a tag semver mais especifica que aponta para o mesmo commit (no `dorny/test-reporter` e `v1`). O pin
nao audita o conteudo: impede que a tag seja movida depois, mas nao prova que o commit de hoje e confiavel. O Trivy estava
referenciado como `0.28.0`, tag que nao existe no repositorio da action (a real e `v0.28.0`); o pin aponta para a `v0.28.0`.
O Dependabot de `github-actions` propoe as atualizacoes mantendo o formato `@sha # versao`.

**Migrations no deploy.** O job `deploy-to-aks` le a connection string do Key Vault na mesma sessao OIDC e roda
`dotnet ef database update` (ferramenta fixada em `dotnet-tools.json`) antes do rollout, com ate 5 tentativas por causa do
Azure SQL serverless. A API em `Production` nunca migra sozinha.

**Ruleset versionado e `Quality` obrigatorio.** `.github/rulesets/protect-main-release.json` replica a protecao dos demais
repositorios (`main` e `release`: sem delete nem force-push, PR obrigatorio) e exige os checks `Build & Test` e `Quality`;
nao ha script para aplica-lo: o JSON versionado e a entrada direta da GitHub CLI, executada manualmente na publicacao e so depois do
primeiro CI (o GitHub so oferece como obrigatorio um check que ja apareceu):
`gh api --method POST repos/willianrmattos/OficinaMecanica.Billing/rulesets --input .github/rulesets/protect-main-release.json`.
Sem o `SONAR_TOKEN` o job `Quality` reprova e, exigido pelo ruleset, bloqueia todo PR.

**Manifests.** `k8s/billing/` com um unico placeholder, `${IMAGE}`, renderizado por `envsubst` com a imagem do sha antes do
`kubectl apply` (o Deployment nasce com a tag certa e nenhum apply regride a imagem); segredos so por
SecretProviderClass/Key Vault; Ingress por prefixo `/billing` com rewrite; `strategy.maxSurge: 0` por causa do no unico.

**Prontidao: banco derruba, broker so rebaixa.** `/health` e liveness puro (200 sempre, sem dependencias: uma queda de
dependencia nao pode reiniciar o pod) e `/health/ready` roda os checks da tag `ready`:
- `sqlserver` falha como `Unhealthy` e a resposta e **503**: sem o banco a API nao aceita escrita (o outbox mora nele), entao o
  pod sai de rotacao.
- `rabbitmq` (conexao AMQP de verdade, so com `Mensageria__Transporte=RabbitMq`) e `masstransit-bus` falham como `Degraded` e a
  resposta continua **200**: com o outbox transacional a API segue aceitando escrita e a mensagem espera no banco, entao tirar o
  pod de rotacao por causa do broker transformaria uma queda do RabbitMQ em indisponibilidade total (e o Mercado Pago
  receberia 5xx no webhook). O `Degraded` aparece no corpo JSON, que traz so nome, status e descricao fixa (nunca a excecao, que
  carrega host, porta e credencial).
- Cada check desiste em 3 s, inclusive contra um servidor que aceita a TCP e nao responde (o cliente AMQP sozinho leva ~8 s,
  por isso o `WaitAsync` e o fechamento da conexao que completar depois do limite), para caber no `timeoutSeconds: 5` da
  readiness. `periodSeconds: 30` so limita a carga das probes; o auto-pause do Azure SQL serverless nao e evitado por elas (o
  outbox consulta o banco a cada segundo), entao a probe sozinha nao acorda nem segura o banco.
- **Cache de 5 s** (`Health__ProntidaoCacheSegundos`; 0 desliga e so os testes de infraestrutura usam) nos resultados de
  `sqlserver` e `rabbitmq`: a rota `billingserver` da APIM repassa qualquer path, entao `/health/ready` e anonimo e alcancavel
  da internet, e sem cache cada requisicao abriria uma conexao SQL e uma AMQP (vetor de esgotamento de conexoes). Com o cache o
  custo e de 1 conexao SQL + 1 AMQP a cada 5 s, qualquer que seja a taxa de requisicoes; ele e menor que o `periodSeconds`, entao
  cada probe do kubelet ainda ve uma medicao nova. Requisicoes simultaneas esperam uma unica avaliacao, e excecao/cancelamento
  nunca e guardado.
- O `startupProbe` em `/health` aceita 120 s (24 x 5 s): o Kestrel so abre a porta depois de o MassTransit subir (ate 60 s com o
  broker lento).

## Consequencias
### Positivas
- Nenhum merge em `main`/`release` sem build, testes de infraestrutura reais, cobertura >= 80% e Quality Gate aprovado.
- O que o Trivy aprova e exatamente o que vai para o ACR; nenhuma imagem com vulnerabilidade corrigivel e publicada.
- O gate de cobertura existe no repositorio (script) e no Sonar: uma falha de configuracao do SaaS nao deixa o limite sem
  guarda.
- Nenhum segredo de longa duracao do Azure; o pipeline de um servico nao consegue agir com a identidade de outro.
- `scripts/ci-local.sh` reproduz o job `Build & Test` na maquina do desenvolvedor, com os mesmos scripts.
- O contrato publico fica versionado (`docs/openapi.json`) e muda junto com o PR que altera a API.

### Negativas
- Testes de infraestrutura baixam imagens grandes (SQL Server) e tornam o `Build & Test` mais lento; o job `quality`
  compila de novo porque o scanner precisa instrumentar o build.
- O check `Quality` pulado (PR de fork) conta como satisfeito para o GitHub: PR de fork nao passa pelo Sonar e depende de
  revisao.
- A migration roda com a versao antiga ainda no ar, e rollback de imagem nao desfaz migration: as migrations precisam ser
  compativeis com a versao anterior (expandir antes de contrair).
- Com uma replica e `maxSurge: 0` ha uma janela sem pod a cada deploy (ate o `startupProbe` passar); os comandos da saga
  esperam no RabbitMQ e o Mercado Pago reenvia o webhook que receber 5xx.
- O runner do GitHub precisa alcancar o Azure SQL (regra de firewall do servidor), a mesma premissa do monolito.
- Antes da publicacao o workflow so foi validado estaticamente e pelos scripts, e as actions estao fixadas por SHA sem que o
  conteudo de cada commit fixado tenha sido auditado (ver a decisao "Actions por SHA").
- Desvio do plano (secao 8, "testes de Infra em job separado"): os testes de Infra rodam num step proprio do MESMO job
  `Build & Test`. O gate de cobertura exige o OpenCover de todos os projetos no mesmo workspace; um job de Infra reconstruiria
  a solucao, transferiria artifacts e obrigaria um terceiro job para unir e aplicar o gate, sem ganho de isolamento (o runner e
  o mesmo), e os nomes dos checks obrigatorios ficariam menos estaveis. O resultado de cada grupo continua separado por step,
  `.trx` e `dorny/test-reporter`. Se o tempo do `Build & Test` virar problema, a separacao e o proximo passo.
- O `ci.yml` so tem testes para a separacao de privilegios (`verificar-workflow.sh`) e o `actionlint`: a ordem dos jobs e as
  condicoes `if` so se provam na primeira execucao real.
- **Broker fora nao tira o pod de rotacao**: com o RabbitMQ parado o `/health/ready` segue 200 e so o corpo mostra `rabbitmq`
  `Degraded`; quem monitora so sabe pelo corpo. O alerta (e um gauge de mensagens pendentes no outbox) ainda nao existe como
  metrica: fica pendente. Em contrapartida, banco fora derruba a prontidao (503) e o pod sai de rotacao ate o banco voltar; com o
  Azure SQL serverless acordando, a probe pode ficar em 503 por alguns ciclos.
- **`/health/ready` anonimo exposto pela APIM**: a rota repassa qualquer path, entao qualquer cliente da internet pode chamar
  `/billingserver/health/ready`. O cache de 5 s limita o custo a uma conexao SQL e uma AMQP a cada 5 s e o corpo nao vaza
  excecao, mas o endpoint continua publico. Mitigacao no `OficinaMecanica.Infra` (ADR 0007 de la): a policy da APIM que devolve 404
  para `/health/ready` e `/metrics` na rota publica existe e fica ativa quando o Terraform do Infra for aplicado (ainda nao foi);
  a probe do kubelet bate direto no pod e nunca passa pela APIM.

## Alternativas consideradas
- **Pipeline unico para todos os servicos**: acopla os repositorios e contraria o requisito de CI/CD independente.
- **Rodar os testes duas vezes** (um step de testes e outro para cobertura): dobra o tempo, principalmente com os testes de
  infraestrutura; a coleta junto do teste entrega o mesmo numero.
- **Gate de cobertura so no SonarCloud**: depende de configuracao externa; o script no repositorio mantem o limite
  verificavel localmente e no CI.
- **Trivy depois do push**: a imagem vulneravel ja estaria no registry quando o scan reprovasse.
- **Scan e push no mesmo job**: mais simples, mas o job com `id-token: write` executaria o Trivy e as actions do Docker, de
  terceiros, com acesso possivel ao token OIDC (e, por ele, a `AcrPush`, `AKS Cluster Admin` e ao Key Vault).
- **Job `Infra Tests` separado**: ver o desvio na secao de consequencias; custa um rebuild, artifacts e um terceiro job para o
  gate, e nao isola nada alem do log.
- **Reconstruir a imagem para o push** (segundo `build-push-action`): o cache costuma reproduzir as camadas, mas nao ha
  garantia de que sejam identicas as escaneadas.
- **Migration na partida da API** (`Database.Migrate()` em `Production`): varias replicas disputariam o esquema e uma
  replica nova alteraria o banco por conta propria.
- **Migration como Job/initContainer no cluster**: exige a connection string de escrita DDL dentro do cluster e mais pecas;
  o CI ja tem a identidade e o acesso ao secret. O `dotnet ef migrations bundle` e uma evolucao possivel.
- **Manifest com `:latest` mais `kubectl set image`**: cada apply voltaria o Deployment para `latest` e o `set image`
  disparava um segundo rollout, com duas indisponibilidades seguidas no no unico.
- **Service principal com client secret em vez de OIDC**: segredo de longa duracao para rotacionar e vazar.
- **Broker fora como `Unhealthy` (503)**: o pod sairia de rotacao em toda queda do RabbitMQ e a API deixaria de aceitar escrita
  e webhook que o outbox absorveria sem perda.
- **`/health/ready` sem cache**: cada requisicao anonima abriria uma conexao SQL e uma AMQP; o cache de 5 s limita o custo sem
  esconder a queda por mais que o `periodSeconds` das probes.
- **Readiness com o limite padrao de 1 s**: os checks levam ate 3 s, entao a probe falharia sem o banco ou o broker estarem fora.
