#!/usr/bin/env bash
# Testes dos scripts que decidem se o CI aprova ou reprova (gate de cobertura, guardrail do MassTransit, verificador do
# workflow, conferencia do OpenAPI e validacao dos manifests). Um gate que so e visto passando num CI saudavel nao prova
# nada: aqui cada script recebe entradas sinteticas e o codigo de saida de cada caso e conferido, inclusive os caminhos de
# FALHA. Alem dos scripts de gate isolados, o cobertura.sh e exercitado de ponta a ponta sobre um OpenCover sintetico
# (e o que prova que o gate esta LIGADO ao relatorio: sem isso, apagar a chamada do gate nao reprovaria nada).
#
# Depois dos casos, os MUTANTES: o script reaplica os casos do script alterado sobre copias com um defeito plantado (o
# gate que sempre aprova, o minimo padrao zerado, o guardrail que aceita o major 9, a chamada do gate apagada do
# cobertura.sh...) e exige que cada defeito seja detectado. Um mutante que sobrevive significa que falta um caso de teste.
#
# Uso: bash scripts/testar-scripts.sh [--sem-mutantes]
# Os casos do cobertura.sh rodam o ReportGenerator fixado (dotnet tool restore, do cache de pacotes) sobre um OpenCover
# sintetico, e os do validar-k8s.sh rodam o kubeconform em Docker (pulados, com aviso, sem Docker); o resto roda em
# segundos, sem build. Tudo que escreve (TestResults, artifacts, docs) escreve numa copia em diretorio temporario, nunca no
# repositorio. O job Build & Test e o ci-local.sh chamam este script.
# shellcheck disable=SC2016  # as expressoes sed dos mutantes ficam em aspas simples de proposito: contem $ literais
set -uo pipefail

RAIZ="$(cd "$(dirname "$0")/.." && pwd)"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

COM_MUTANTES=true
for argumento in "$@"; do
  case "$argumento" in
    --sem-mutantes) COM_MUTANTES=false ;;
    *) echo "Argumento desconhecido: $argumento (use --sem-mutantes)" >&2; exit 2 ;;
  esac
done

CASOS=0
FALHAS=0
SILENCIOSO=false
# Dentro de um mutante basta UM caso reprovar: os seguintes nao rodam (alguns sobem o ReportGenerator ou o kubeconform).
FALHAS_ANTES_DO_MUTANTE=0
PRIMEIRO_CASO_QUE_REPROVOU=""

TEM_DOCKER=false
if docker info > /dev/null 2>&1; then
  TEM_DOCKER=true
fi

# esperar <codigo-esperado|nao-zero> <descricao> <comando...>: roda o comando e confere o codigo de saida.
esperar() {
  local esperado="$1" descricao="$2" real aprovado=false
  shift 2
  if [ "$SILENCIOSO" = true ] && [ "$FALHAS" -gt "$FALHAS_ANTES_DO_MUTANTE" ]; then
    return 0
  fi
  # Hermetico: o CI e o ci-local exportam COBERTURA_MINIMA; sem limpar, o "minimo padrao" dos casos viraria o do ambiente.
  env -u COBERTURA_MINIMA -u MASSTRANSIT_MAJOR_PERMITIDO "$@" > "$TMP/saida.txt" 2>&1
  real=$?
  CASOS=$((CASOS + 1))
  if [ "$esperado" = "nao-zero" ]; then
    [ "$real" -ne 0 ] && aprovado=true
  elif [ "$real" -eq "$esperado" ]; then
    aprovado=true
  fi
  if [ "$aprovado" = true ]; then
    [ "$SILENCIOSO" = true ] || echo "  ok      $descricao"
  else
    FALHAS=$((FALHAS + 1))
    [ -n "$PRIMEIRO_CASO_QUE_REPROVOU" ] || PRIMEIRO_CASO_QUE_REPROVOU="$descricao"
    [ "$SILENCIOSO" = true ] || { echo "  FALHOU  $descricao (esperado exit $esperado, obtido $real)"; sed 's/^/            | /' "$TMP/saida.txt"; }
  fi
}

# Summary.txt sintetico no formato do ReportGenerator: resumo <arquivo> <cobertas> <cobertaveis> <"Line coverage" arredondado>
resumo() {
  printf 'Summary\n  Generated on: sintetico\n  Line coverage: %s\n  Covered lines: %s\n  Uncovered lines: %s\n  Coverable lines: %s\n  Total lines: 99999\n' \
    "$4" "$2" "$(($3 - $2))" "$3" > "$1"
}

# Saida sintetica de 'dotnet list package --include-transitive' com uma linha de MassTransit por projeto.
saida_do_dotnet() {
  local arquivo="$1"
  shift
  {
    echo "Project 'Servico.Domain' has the following package references"
    echo "   [net8.0]: No packages were found for this framework."
    local i=0 linha
    for linha in "$@"; do
      i=$((i + 1))
      echo "Project 'Servico.Projeto$i' has the following package references"
      echo "   [net8.0]: "
      echo "   Top-level Package      Requested   Resolved"
      echo "   > Azure.Identity       1.21.0      1.21.0"
      echo "$linha"
      echo
    done
  } > "$arquivo"
}

casos_do_gate_e_do_guardrail() {
  local s="$1"   # diretorio com os scripts sob teste
  local gate="$s/verificar-cobertura.sh" guardrail="$s/verificar-masstransit.sh"
  local d="$TMP/entradas"
  mkdir -p "$d"

  # --- gate de cobertura ---
  resumo "$d/79_9.txt"   799   1000   "79.9%"
  resumo "$d/80_0.txt"   800   1000   "80%"
  resumo "$d/79_96.txt"  7996  10000  "80%"      # o ReportGenerator arredonda 79,96 para 80%
  resumo "$d/80_001.txt" 80001 100000 "80%"
  resumo "$d/85.txt"     85    100    "85%"
  printf 'Summary\n  Line coverage: 99.5%%\n' > "$d/sem_contagem.txt"
  printf 'Summary\n  Uncovered lines: 5\n  Coverable lines: 10\n' > "$d/so_nao_cobertas.txt"
  resumo "$d/zero.txt" 0 0 "0%"

  esperar 1 "gate: 79,9% reprova com o minimo padrao"                     bash "$gate" "$d/79_9.txt"
  esperar 0 "gate: exatamente 80,0% aprova (limite inclusivo)"            bash "$gate" "$d/80_0.txt"
  esperar 1 "gate: 79,96% reprova mesmo arredondando para 80% no resumo"  bash "$gate" "$d/79_96.txt"
  esperar 0 "gate: 80,001% aprova"                                        bash "$gate" "$d/80_001.txt"
  esperar 2 "gate: sem contagem de linhas nao aprova (exit 2)"            bash "$gate" "$d/sem_contagem.txt"
  esperar 2 "gate: 'Uncovered lines' nao e tomado por 'Covered lines'"    bash "$gate" "$d/so_nao_cobertas.txt"
  esperar 2 "gate: zero linhas cobertaveis nao aprova (exit 2)"           bash "$gate" "$d/zero.txt"
  esperar 2 "gate: arquivo inexistente (exit 2)"                          bash "$gate" "$d/nao_existe.txt"
  esperar 1 "gate: COBERTURA_MINIMA vazio cai no padrao 80 (79,9% reprova)" env COBERTURA_MINIMA= bash "$gate" "$d/79_9.txt"
  esperar 1 "gate: COBERTURA_MINIMA ausente cai no padrao 80 (79,9% reprova)" env -u COBERTURA_MINIMA bash "$gate" "$d/79_9.txt"
  esperar 0 "gate: COBERTURA_MINIMA=70 aprova 79,9%"                      env COBERTURA_MINIMA=70 bash "$gate" "$d/79_9.txt"
  esperar 1 "gate: o 2o argumento vence a variavel (90 reprova 85%)"      env COBERTURA_MINIMA=70 bash "$gate" "$d/85.txt" 90
  esperar 2 "gate: COBERTURA_MINIMA nao numerica (exit 2)"                env COBERTURA_MINIMA=abc bash "$gate" "$d/85.txt"

  # --- guardrail do MassTransit ---
  local mt8="   > MassTransit                8.5.12     8.5.12"
  local abs8="   > MassTransit.Abstractions                8.5.12"
  saida_do_dotnet "$d/so8.txt"           "$mt8" "$abs8"
  saida_do_dotnet "$d/direto9.txt"       "   > MassTransit                9.0.0      9.0.0"
  saida_do_dotnet "$d/transitivo9.txt"   "$mt8" "   > MassTransit.Abstractions                9.0.0"
  saida_do_dotnet "$d/transitivo9_so.txt" "$abs8" "   > MassTransit.RabbitMQ                    9.1.0"
  saida_do_dotnet "$d/major10.txt"       "   > MassTransit.Abstractions                10.0.0"
  saida_do_dotnet "$d/efcore8.txt"       "$mt8" "   > MassTransit.EntityFrameworkCore         8.5.12     8.5.12"
  saida_do_dotnet "$d/efcore9_direto.txt" "$mt8" "   > MassTransit.EntityFrameworkCore         9.0.0      9.0.0"
  saida_do_dotnet "$d/efcore9_transitivo.txt" "$mt8" "   > MassTransit.EntityFrameworkCore         9.0.0"
  saida_do_dotnet "$d/prerelease8.txt"   "   > MassTransit                8.5.12-develop.1     8.5.12-develop.1"
  saida_do_dotnet "$d/sem_masstransit.txt"
  sed 's/$/\r/' "$d/so8.txt" > "$d/so8_crlf.txt"
  sed 's/$/\r/' "$d/direto9.txt" > "$d/direto9_crlf.txt"

  esperar 0 "guardrail: so MassTransit 8.x (direto e transitivo) aprova"              bash "$guardrail" --entrada "$d/so8.txt"
  esperar 1 "guardrail: MassTransit 9.x direto reprova"                               bash "$guardrail" --entrada "$d/direto9.txt"
  esperar 1 "guardrail: MassTransit.Abstractions 9.x so transitivo reprova"           bash "$guardrail" --entrada "$d/transitivo9.txt"
  esperar 1 "guardrail: MassTransit.RabbitMQ 9.x transitivo, sem nenhum direto, reprova" bash "$guardrail" --entrada "$d/transitivo9_so.txt"
  esperar 1 "guardrail: major 10 tambem reprova (qualquer major diferente de 8)"      bash "$guardrail" --entrada "$d/major10.txt"
  esperar 0 "guardrail: MassTransit.EntityFrameworkCore 8.x aprova"                   bash "$guardrail" --entrada "$d/efcore8.txt"
  esperar 1 "guardrail: MassTransit.EntityFrameworkCore 9.x direto reprova"           bash "$guardrail" --entrada "$d/efcore9_direto.txt"
  esperar 1 "guardrail: MassTransit.EntityFrameworkCore 9.x so transitivo reprova"    bash "$guardrail" --entrada "$d/efcore9_transitivo.txt"
  esperar 0 "guardrail: versao 8.x com sufixo de pre-release aprova"                  bash "$guardrail" --entrada "$d/prerelease8.txt"
  esperar 3 "guardrail: sem nenhum MassTransit* o guardrail nao fica vazio (exit 3)"  bash "$guardrail" --entrada "$d/sem_masstransit.txt"
  esperar 0 "guardrail: saida com CRLF (Windows) e lida igual (8.x aprova)"           bash "$guardrail" --entrada "$d/so8_crlf.txt"
  esperar 1 "guardrail: saida com CRLF e 9.x reprova"                                 bash "$guardrail" --entrada "$d/direto9_crlf.txt"
  esperar 0 "guardrail: MASSTRANSIT_MAJOR_PERMITIDO=9 aprova 9.x"                     env MASSTRANSIT_MAJOR_PERMITIDO=9 bash "$guardrail" --entrada "$d/direto9.txt"
  esperar 1 "guardrail: MASSTRANSIT_MAJOR_PERMITIDO=9 reprova 8.x"                    env MASSTRANSIT_MAJOR_PERMITIDO=9 bash "$guardrail" --entrada "$d/so8.txt"
  esperar 2 "guardrail: arquivo de entrada inexistente nao aprova (exit 2)"           bash "$guardrail" --entrada "$d/nao_existe.txt"

  # --- separacao de privilegios do ci.yml ---
  local verificador="$s/verificar-workflow.sh"
  fluxo() {  # fluxo <arquivo> <permissions do workflow> <steps do scan-image> <steps do push> (uma action por argumento)
    local arquivo="$1" nivel="$2" scan="$3" push="$4"
    {
      echo "name: CI"
      echo "$nivel"
      echo "jobs:"
      echo "  build-and-test:"
      echo "    steps:"
      echo "      - uses: dorny/test-reporter@v1"
      echo "  scan-image:"
      echo "    permissions:"
      echo "      contents: read"
      echo "$scan"
      echo "  build-and-push-image:"
      echo "    permissions:"
      echo "      id-token: write"
      echo "      contents: read"
      echo "    steps:"
      echo "$push"
    } > "$arquivo"
  }
  local scan_ok=$'    steps:\n      - uses: docker/build-push-action@v6\n      - uses: aquasecurity/trivy-action@0.28.0'
  local push_ok=$'      - uses: actions/download-artifact@v4\n      - uses: azure/login@v2'
  local nivel_ok=$'permissions:\n  contents: read'
  fluxo "$d/fluxo_ok.yml"            "$nivel_ok" "$scan_ok" "$push_ok"
  fluxo "$d/fluxo_scan_oidc.yml"     "$nivel_ok" "$scan_ok"$'\n    permissions:\n      id-token: write' "$push_ok"
  fluxo "$d/fluxo_push_trivy.yml"    "$nivel_ok" "$scan_ok" "$push_ok"$'\n      - uses: aquasecurity/trivy-action@0.28.0'
  fluxo "$d/fluxo_push_aspas.yml"    "$nivel_ok" "$scan_ok" "$push_ok"$'\n      - name: x\n        uses: "docker/setup-buildx-action@v3"'
  fluxo "$d/fluxo_comentario.yml"    "$nivel_ok" $'    # nao recebe id-token: so le o repositorio\n'"$scan_ok" "$push_ok"
  fluxo "$d/fluxo_nivel_oidc.yml"    $'permissions:\n  id-token: write' "$scan_ok" "$push_ok"
  sed 's/$/\r/' "$d/fluxo_ok.yml" > "$d/fluxo_ok_crlf.yml"
  sed 's/$/\r/' "$d/fluxo_push_trivy.yml" > "$d/fluxo_push_trivy_crlf.yml"
  printf 'name: CI\njobs:\n' > "$d/fluxo_sem_jobs.yml"

  esperar 0 "workflow: scan sem id-token e push so com actions/* e azure/* aprova"      bash "$verificador" "$d/fluxo_ok.yml"
  esperar 1 "workflow: scan-image com id-token reprova (action de terceiros com OIDC)"  bash "$verificador" "$d/fluxo_scan_oidc.yml"
  esperar 1 "workflow: job de push executando a action do Trivy reprova"                bash "$verificador" "$d/fluxo_push_trivy.yml"
  esperar 1 "workflow: job de push com action de terceiros entre aspas reprova"         bash "$verificador" "$d/fluxo_push_aspas.yml"
  esperar 0 "workflow: id-token citado so em comentario nao conta"                      bash "$verificador" "$d/fluxo_comentario.yml"
  esperar 1 "workflow: id-token no nivel do workflow (vale para todos os jobs) reprova" bash "$verificador" "$d/fluxo_nivel_oidc.yml"
  esperar 0 "workflow: fim de linha CRLF e lido igual (aprova)"                         bash "$verificador" "$d/fluxo_ok_crlf.yml"
  esperar 1 "workflow: fim de linha CRLF e job de push com Trivy reprova"               bash "$verificador" "$d/fluxo_push_trivy_crlf.yml"
  esperar 2 "workflow: sem nenhum job nao aprova (exit 2)"                              bash "$verificador" "$d/fluxo_sem_jobs.yml"
  esperar 2 "workflow: arquivo inexistente (exit 2)"                                    bash "$verificador" "$d/nao_existe.yml"
}

# --- Casos dos demais scripts: cada um roda numa COPIA (sandbox) com os scripts sob teste, porque os scripts fazem 'cd' para a
# raiz que contem a pasta scripts/ e escrevem em TestResults/, artifacts/ e docs/ dela.

# novo_sandbox <diretorio-com-os-scripts>: imprime a raiz de uma copia temporaria com os scripts, o manifesto das ferramentas
# e o nuget.config (o 'dotnet tool restore' dos scripts resolve do mesmo jeito que no repositorio).
novo_sandbox() {
  local raiz
  raiz="$(mktemp -d "$TMP/sandbox.XXXXXX")"
  cp -R "$1" "$raiz/scripts"
  cp "$RAIZ/dotnet-tools.json" "$RAIZ/nuget.config" "$raiz/"
  echo "$raiz"
}

# opencover <destino> <Arquivo.cs:cobertas:total>...: OpenCover sintetico no formato do coverlet, com uma classe por arquivo e
# um ponto de sequencia por linha (o ReportGenerator tira as linhas cobertas e cobertaveis deles). O modulo se chama
# OficinaMecanica.Billing.Domain para passar no filtro de assemblies do cobertura.sh.
opencover() {
  local destino="$1"
  shift
  mkdir -p "$(dirname "$destino")"
  printf '%s\n' "$@" | awk -F: '
    function resumo(c, t) {
      return "<Summary numSequencePoints=\"" t "\" visitedSequencePoints=\"" c "\" numBranchPoints=\"0\" visitedBranchPoints=\"0\" sequenceCoverage=\"0\" branchCoverage=\"100\" maxCyclomaticComplexity=\"1\" minCyclomaticComplexity=\"1\" visitedClasses=\"1\" numClasses=\"1\" visitedMethods=\"1\" numMethods=\"1\" />"
    }
    { arquivo[NR] = $1; cobertas[NR] = $2; total[NR] = $3 }
    END {
      print "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
      print "<CoverageSession>"
      print "  " resumo(0, 0)
      print "  <Modules><Module hash=\"00000000-0000-0000-0000-000000000001\">"
      print "    <ModulePath>OficinaMecanica.Billing.Domain.dll</ModulePath><ModuleTime>2026-01-01T00:00:00</ModuleTime>"
      print "    <ModuleName>OficinaMecanica.Billing.Domain</ModuleName>"
      print "    <Files>"
      for (i = 1; i <= NR; i++) print "      <File uid=\"" i "\" fullPath=\"/sintetico/" arquivo[i] "\" />"
      print "    </Files>"
      print "    <Classes>"
      ponto = 0
      for (i = 1; i <= NR; i++) {
        print "      <Class>" resumo(cobertas[i], total[i]) "<FullName>OficinaMecanica.Billing.Domain.Classe" i "</FullName><Methods>"
        print "        <Method cyclomaticComplexity=\"1\" nPathComplexity=\"1\" sequenceCoverage=\"0\" branchCoverage=\"100\" isConstructor=\"False\" isGetter=\"False\" isSetter=\"False\" isStatic=\"False\">"
        print "          " resumo(cobertas[i], total[i]) "<MetadataToken /><Name>System.Void Classe" i "::Metodo()</Name><FileRef uid=\"" i "\" /><SequencePoints>"
        for (k = 1; k <= total[i]; k++) {
          ponto++
          printf "            <SequencePoint vc=\"%d\" uspid=\"%d\" ordinal=\"%d\" sl=\"%d\" sc=\"1\" el=\"%d\" ec=\"2\" bec=\"0\" bev=\"0\" fileid=\"%d\" />\n", (k <= cobertas[i] ? 1 : 0), ponto, k - 1, k, k, i
        }
        print "          </SequencePoints><BranchPoints />"
        print "        </Method></Methods></Class>"
      }
      print "    </Classes>"
      print "  </Module></Modules>"
      print "</CoverageSession>"
    }' > "$destino"
}

# cobertura.sh: o gate (verificar-cobertura.sh) e testado acima so com resumos sinteticos; aqui se prova que o cobertura.sh o
# CHAMA, sobre o numero "com exclusoes", no caminho 'relatorio' (apagar essa chamada deixaria o CI aprovando qualquer cobertura).
casos_do_cobertura() {
  local s="$1" sb

  sb="$(novo_sandbox "$s")"
  esperar 2 "cobertura.sh: fase desconhecida (exit 2)" bash "$sb/scripts/cobertura.sh" fase-invalida
  esperar 2 "cobertura.sh relatorio: sem nenhum coverage.opencover.xml nao aprova (exit 2)" bash "$sb/scripts/cobertura.sh" relatorio

  sb="$(novo_sandbox "$s")"
  opencover "$sb/TestResults/sintetico/g/coverage.opencover.xml" Sintetico.cs:79:100
  esperar 1 "cobertura.sh relatorio: 79% de linhas reprova (o gate esta ligado ao relatorio)" bash "$sb/scripts/cobertura.sh" relatorio

  sb="$(novo_sandbox "$s")"
  opencover "$sb/TestResults/sintetico/g/coverage.opencover.xml" Sintetico.cs:81:100
  esperar 0 "cobertura.sh relatorio: 81% de linhas aprova" bash "$sb/scripts/cobertura.sh" relatorio
  esperar 0 "cobertura.sh relatorio: escreve o resumo em Markdown com as duas medicoes (Job Summary)" \
    grep -q "Com exclusoes" "$sb/TestResults/resumo-cobertura.md"

  # Os quatro padroes excluidos sem nenhuma linha coberta: bruto 90/290 = 31% (reprovaria), com exclusoes 90/100 = 90% (o numero
  # do gate). Remover qualquer um dos quatro filtros leva o numero a 90/150 = 60% e reprova.
  sb="$(novo_sandbox "$s")"
  opencover "$sb/TestResults/sintetico/g/coverage.opencover.xml" Sintetico.cs:90:100 Migrations/Inicial.cs:0:50 Program.cs:0:50 \
    DependencyInjection.cs:0:50 Data/Configurations/Config.cs:0:50
  esperar 0 "cobertura.sh relatorio: o gate vale para o numero com exclusoes (Migrations, Program.cs, DependencyInjection e EF Configurations ficam fora)" \
    bash "$sb/scripts/cobertura.sh" relatorio

  # O aviso de [ExcludeFromCodeCoverage]: lista os arquivos de src/ (fora de obj/ e bin/) e escreve a contagem no resumo.
  sb="$(novo_sandbox "$s")"
  opencover "$sb/TestResults/sintetico/g/coverage.opencover.xml" Sintetico.cs:81:100
  mkdir -p "$sb/src/Dominio/obj"
  printf '[ExcludeFromCodeCoverage]\npublic class A {}\n[ExcludeFromCodeCoverage]\npublic class B {}\n' > "$sb/src/Dominio/Excluida.cs"
  printf '[ExcludeFromCodeCoverage]\n' > "$sb/src/Dominio/obj/Gerado.cs"
  esperar 0 "cobertura.sh relatorio: lista o arquivo de src/ com [ExcludeFromCodeCoverage] e ignora o que esta em obj/" \
    bash -c 'bash "$1" relatorio > "$2" 2>&1 && grep -q "AVISO: \[ExcludeFromCodeCoverage\]" "$2" && grep -q "src/Dominio/Excluida.cs:1:" "$2" && ! grep -q "Gerado.cs" "$2"' \
    _ "$sb/scripts/cobertura.sh" "$sb/relatorio.txt"
  esperar 0 "cobertura.sh relatorio: o resumo em Markdown conta 2 ocorrencias de [ExcludeFromCodeCoverage]" \
    grep -Fq "| Ocorrencias de [ExcludeFromCodeCoverage] em src/ | 2 |" "$sb/TestResults/resumo-cobertura.md"

  sb="$(novo_sandbox "$s")"
  opencover "$sb/TestResults/sintetico/g/coverage.opencover.xml" Sintetico.cs:79:100
  esperar 0 "cobertura.sh relatorio: COBERTURA_MINIMA=70 aprova 79% (a variavel chega ao gate)" env COBERTURA_MINIMA=70 bash "$sb/scripts/cobertura.sh" relatorio
}

# validar-k8s.sh: o kubeconform roda em Docker e baixa o schema do Kubernetes (os mesmos requisitos do passo do CI).
# deployment_sintetico [linha-do-spec]: Deployment minimo com o placeholder ${IMAGE} e, no spec, a linha dada (padrao: um campo valido).
deployment_sintetico() {
  cat <<YAML
apiVersion: apps/v1
kind: Deployment
metadata:
  name: sintetico
spec:
  ${1:-replicas: 1}
  selector:
    matchLabels:
      app: sintetico
  template:
    metadata:
      labels:
        app: sintetico
    spec:
      containers:
        - name: api
          image: \${IMAGE}
YAML
}

casos_do_k8s() {
  local s="$1" sb dir

  sb="$(novo_sandbox "$s")"
  esperar nao-zero "validar-k8s: diretorio de manifests inexistente nao aprova" env K8S_DIR="$sb/nao_existe" bash "$sb/scripts/validar-k8s.sh"

  if [ "$TEM_DOCKER" = false ]; then
    [ "$SILENCIOSO" = true ] || echo "  pulado  validar-k8s: casos com o kubeconform (Docker indisponivel)"
    return 0
  fi

  dir="$sb/manifests-ok"
  mkdir -p "$dir"
  {
    deployment_sintetico
    printf -- '---\napiVersion: secrets-store.csi.x-k8s.io/v1\nkind: SecretProviderClass\nmetadata:\n  name: sintetico\nspec:\n  provider: azure\n'
  } > "$dir/manifests.yaml"
  esperar 0 "validar-k8s: Deployment valido e CRD sem schema (SecretProviderClass ignorado) aprovam" env K8S_DIR="$dir" bash "$sb/scripts/validar-k8s.sh"

  # O mesmo Deployment com um campo que o schema nao conhece: so o -strict o reprova.
  dir="$sb/manifests-invalidos"
  mkdir -p "$dir"
  deployment_sintetico "campoInexistente: 1" > "$dir/manifests.yaml"
  esperar nao-zero "validar-k8s: campo desconhecido no manifest reprova (-strict)" env K8S_DIR="$dir" bash "$sb/scripts/validar-k8s.sh"
}

# exportar-swagger.sh --verificar: roda com um 'dotnet' de mentira (so 'tool restore' e 'swagger tofile'), que grava um documento
# sintetico no lugar do OpenAPI da API. Assim a LOGICA do script (normalizar fim de linha, comparar com docs/openapi.json, falhar
# se divergir ou se faltar) e testada sem build, sem Docker e sem subir a API.
dotnet_de_mentira() {
  mkdir -p "$1"
  cat > "$1/dotnet" <<'DOTNET'
#!/usr/bin/env bash
case "$1 $2" in
  "tool restore") exit 0 ;;
  "swagger tofile") printf '%b' "$DOCUMENTO_SINTETICO" > "$4"; exit 0 ;;
esac
echo "dotnet de mentira: comando inesperado: $*" >&2
exit 99
DOTNET
  chmod +x "$1/dotnet"
}

casos_do_swagger() {
  local s="$1" sb
  # O gerador escreve CRLF no Windows e o compilador deixa "\r\n" dentro das strings dos comentarios XML: o script normaliza os dois.
  local gerado='{\r\n  "info": "linha1\\r\\nlinha2"\r\n}\r\n'
  local versionado='{\n  "info": "linha1\\nlinha2"\n}\n'
  local divergente='{\n  "info": "outra descricao"\n}\n'

  sb="$(novo_sandbox "$s")"
  dotnet_de_mentira "$sb/bin"
  local chamar=(env "PATH=$sb/bin:$PATH" "DOCUMENTO_SINTETICO=$gerado" bash "$sb/scripts/exportar-swagger.sh")

  esperar 2 "exportar-swagger: argumento desconhecido (exit 2)" "${chamar[@]}" --nao-existe

  mkdir -p "$sb/docs"
  printf '%b' "$versionado" > "$sb/docs/openapi.json"
  esperar 0 "exportar-swagger --verificar: versionado em dia (gerado com CRLF e \\r\\n nas strings equivale ao LF) aprova" "${chamar[@]}" --verificar --sem-build

  printf '%b' "$divergente" > "$sb/docs/openapi.json"
  esperar 1 "exportar-swagger --verificar: versionado divergente da API reprova" "${chamar[@]}" --verificar --sem-build

  rm -f "$sb/docs/openapi.json"
  esperar 1 "exportar-swagger --verificar: sem docs/openapi.json reprova" "${chamar[@]}" --verificar --sem-build

  esperar 0 "exportar-swagger: sem --verificar regrava docs/openapi.json" "${chamar[@]}" --sem-build
  printf '%b' "$versionado" > "$sb/esperado.json"
  esperar 0 "exportar-swagger: o arquivo regravado esta normalizado (LF, \\r\\n das strings virou \\n)" cmp -s "$sb/docs/openapi.json" "$sb/esperado.json"
}

casos_do_ci_local() {
  local sb
  sb="$(novo_sandbox "$1")"
  esperar 2 "ci-local: argumento desconhecido (exit 2)" bash "$sb/scripts/ci-local.sh" --nao-existe
}

# O gate de cobertura do CI vale o que a variavel COBERTURA_MINIMA do env do ci.yml disser: reduzir esse numero (80 para 10) deixaria
# o gate aprovando quase tudo sem que nenhum outro caso reprovasse. Recebe o caminho do ci.yml (o real ou uma copia com defeito
# plantado); a linha e comparada inteira, na indentacao do bloco env, e tolera o CR de um checkout com fim de linha do Windows.
casos_do_gate_no_workflow() {
  esperar 0 "ci.yml: o gate de cobertura do CI exporta COBERTURA_MINIMA: \"80\" (reduzir o valor tem que reprovar)" \
    grep -Eq '^  COBERTURA_MINIMA: "80"[[:space:]]*$' "$1"
}

casos_do_repositorio() {
  local ruleset="$RAIZ/.github/rulesets/protect-main-release.json" workflow="$RAIZ/.github/workflows/ci.yml"

  esperar 0 "aplicar-ruleset: --dry-run nao chama o GitHub e sai 0"  bash "$RAIZ/scripts/aplicar-ruleset.sh" --dry-run
  esperar 2 "aplicar-ruleset: argumento desconhecido (exit 2)"       bash "$RAIZ/scripts/aplicar-ruleset.sh" --nao-existe

  # Os contextos exigidos pelo ruleset sao os nomes dos jobs do ci.yml: se divergirem, o check obrigatorio nunca aparece
  # e todo PR fica bloqueado.
  local contexto
  while IFS= read -r contexto; do
    esperar 0 "ruleset: o contexto '$contexto' e o name de um job do ci.yml" grep -Fq "name: \"$contexto\"" "$workflow"
  done < <(sed -nE 's/^[[:space:]]*"context":[[:space:]]*"([^"]+)".*/\1/p' "$ruleset")
  esperar 0 "workflow: o ci.yml real respeita a separacao de privilegios (id-token x actions de terceiros)" bash "$RAIZ/scripts/verificar-workflow.sh" "$workflow"
  casos_do_gate_no_workflow "$workflow"
  esperar 0 "ruleset: exige Build & Test" grep -Fq '"context": "Build & Test"' "$ruleset"
  esperar 0 "ruleset: exige Quality"      grep -Fq '"context": "Quality"' "$ruleset"

  # O gate so protege o CI se o workflow o CHAMA, e o ci-local.sh so reproduz o CI se chama os mesmos passos: cada chamada
  # precisa estar nos dois arquivos (apagar a do relatorio de cobertura tiraria o gate dos dois sem reprovar nenhum teste).
  local chamada
  local passos=("scripts/testar-scripts.sh" "scripts/verificar-masstransit.sh" "scripts/validar-k8s.sh"
                "scripts/exportar-swagger.sh --verificar --sem-build" "scripts/cobertura.sh testes-sem-infra"
                "scripts/cobertura.sh testes-infra" "scripts/cobertura.sh relatorio")
  for chamada in "${passos[@]}"; do
    esperar 0 "ci.yml e ci-local.sh chamam os mesmos passos: 'bash $chamada'" \
      bash -c 'grep -Fq "bash $1" "$2" && grep -Fq "bash $1" "$3"' _ "$chamada" "$workflow" "$RAIZ/scripts/ci-local.sh"
  done
}

# mutante <nome> <script> <expressao-sed> [<funcao-de-casos>]: aplica o defeito numa copia do script e exige que algum caso
# da funcao reprove (padrao: os casos do gate e do guardrail; os scripts com casos proprios passam a funcao deles).
MUTANTES=0
SOBREVIVENTES=0
mutante() {
  local nome="$1" script="$2" expressao="$3" funcao="${4:-casos_do_gate_e_do_guardrail}" copia
  MUTANTES=$((MUTANTES + 1))
  copia="$TMP/mutante-$MUTANTES"
  mkdir -p "$copia"
  cp -R "$RAIZ/scripts" "$copia/scripts"
  cp "$copia/scripts/$script" "$copia/original"
  sed -i.bak "$expressao" "$copia/scripts/$script"
  rm -f "$copia/scripts/$script.bak"

  if cmp -s "$copia/original" "$copia/scripts/$script"; then
    echo "  ERRO    mutante '$nome': a expressao nao alterou $script (o teste do mutante esta desatualizado)"
    SOBREVIVENTES=$((SOBREVIVENTES + 1))
    return
  fi

  avaliar_mutante "$nome" "$funcao" "$copia/scripts"
}

# mutante_de_arquivo <nome> <arquivo-relativo-a-raiz> <expressao-sed> <funcao-de-casos>: o mesmo para um arquivo que nao e script (o
# ci.yml): o defeito vai numa copia e a funcao de casos recebe o caminho dela.
mutante_de_arquivo() {
  local nome="$1" arquivo="$2" expressao="$3" funcao="$4" copia
  MUTANTES=$((MUTANTES + 1))
  copia="$TMP/mutante-$MUTANTES"
  mkdir -p "$copia"
  cp "$RAIZ/$arquivo" "$copia/original"
  cp "$RAIZ/$arquivo" "$copia/alterado"
  sed -i.bak "$expressao" "$copia/alterado"
  rm -f "$copia/alterado.bak"

  if cmp -s "$copia/original" "$copia/alterado"; then
    echo "  ERRO    mutante '$nome': a expressao nao alterou $arquivo (o teste do mutante esta desatualizado)"
    SOBREVIVENTES=$((SOBREVIVENTES + 1))
    return
  fi

  avaliar_mutante "$nome" "$funcao" "$copia/alterado"
}

# avaliar_mutante <nome> <funcao-de-casos> <alvo>: roda os casos sobre o alvo com o defeito e exige que algum reprove.
avaliar_mutante() {
  local nome="$1" funcao="$2" alvo="$3"
  local casos_antes=$CASOS falhas_antes=$FALHAS
  FALHAS_ANTES_DO_MUTANTE=$FALHAS
  PRIMEIRO_CASO_QUE_REPROVOU=""
  SILENCIOSO=true
  "$funcao" "$alvo"
  SILENCIOSO=false
  local reprovados=$((FALHAS - falhas_antes))
  # Os casos do mutante nao contam como falha do repositorio: so o fato de o mutante ter sido detectado importa.
  FALHAS=$falhas_antes
  CASOS=$casos_antes

  if [ "$reprovados" -gt 0 ]; then
    echo "  morto   $nome (reprovou: $PRIMEIRO_CASO_QUE_REPROVOU)"
  else
    echo "  SOBREVIVEU  $nome (nenhum caso detectou o defeito)"
    SOBREVIVENTES=$((SOBREVIVENTES + 1))
  fi
}

echo "== Casos: gate de cobertura, guardrail do MassTransit e verificador do workflow"
casos_do_gate_e_do_guardrail "$RAIZ/scripts"
echo "== Casos: cobertura.sh (relatorio e gate ligados, sobre OpenCover sintetico)"
casos_do_cobertura "$RAIZ/scripts"
echo "== Casos: validar-k8s.sh (kubeconform)"
casos_do_k8s "$RAIZ/scripts"
echo "== Casos: exportar-swagger.sh (dotnet de mentira)"
casos_do_swagger "$RAIZ/scripts"
echo "== Casos: ci-local.sh"
casos_do_ci_local "$RAIZ/scripts"
echo "== Casos: ruleset, aplicar-ruleset e ligacao ci.yml x ci-local.sh"
casos_do_repositorio

if [ "$COM_MUTANTES" = true ]; then
  echo "== Mutantes (cada defeito plantado precisa ser detectado)"
  mutante "gate que reprova mas sai 0"                  verificar-cobertura.sh   's/^  exit 1$/  exit 0/'
  mutante "minimo padrao 80 -> 0"                       verificar-cobertura.sh   's/COBERTURA_MINIMA:-80/COBERTURA_MINIMA:-0/'
  mutante "minimo vazio deixa de cair no padrao"        verificar-cobertura.sh   's/\${COBERTURA_MINIMA:-80}/${COBERTURA_MINIMA-80}/'
  mutante "limite inclusivo vira exclusivo (>= -> >)"   verificar-cobertura.sh   's/c \* 100 >= m \* t/c * 100 > m * t/'
  mutante "gate compara o percentual arredondado"       verificar-cobertura.sh   's/exit !(c \* 100 >= m \* t)/exit !(sprintf("%.1f", c * 100 \/ t) + 0 >= m)/'
  mutante "guardrail aceita o major 9"                  verificar-masstransit.sh 's/v\[1\] != permitido/v[1] != permitido \&\& v[1] != 9/'
  mutante "guardrail que reprova mas sai 0"             verificar-masstransit.sh 's/^  exit 1$/  exit 0/'
  mutante "major permitido padrao 8 -> 9"               verificar-masstransit.sh 's/MASSTRANSIT_MAJOR_PERMITIDO:-8/MASSTRANSIT_MAJOR_PERMITIDO:-9/'
  mutante "verificador do workflow que reprova mas sai 0"  verificar-workflow.sh 's/^  exit 1$/  exit 0/'
  mutante "verificador libera as actions docker/* e aquasecurity/*" verificar-workflow.sh 's/(actions|azure)\//(actions|azure|docker|aquasecurity)\//'
  mutante "verificador ignora id-token no nivel do workflow" verificar-workflow.sh 's/{ exit } { print }/{ exit }/'
  mutante "verificador conta comentarios do YAML"       verificar-workflow.sh 's/grep -Ev .^\[\[:space:\]\]\*#. || true/cat || true/'
  mutante "guardrail ignora pacote transitivo"          verificar-masstransit.sh 's/\$1 == ">" && \$2/$1 == ">" \&\& NF == 4 \&\& $2/'

  # cobertura.sh: a ligacao do gate ao relatorio.
  mutante "cobertura.sh: chamada do gate apagada do relatorio" cobertura.sh '/^  bash scripts\/verificar-cobertura.sh /d' casos_do_cobertura
  mutante "cobertura.sh: gate passa a ler o numero bruto"   cobertura.sh 's#verificar-cobertura.sh TestResults/report-com-exclusoes#verificar-cobertura.sh TestResults/report-bruto#' casos_do_cobertura
  mutante "cobertura.sh: relatorio com exclusoes deixa de excluir Migrations" cobertura.sh 's#-\*\*/Migrations/\*\*;##' casos_do_cobertura
  mutante "cobertura.sh: relatorio com exclusoes deixa de excluir Program.cs" cobertura.sh 's#-\*\*/Program\.cs;##' casos_do_cobertura
  mutante "cobertura.sh: relatorio com exclusoes deixa de excluir DependencyInjection" cobertura.sh 's#-\*\*/DependencyInjection\*\.cs;##' casos_do_cobertura
  mutante "cobertura.sh: relatorio com exclusoes deixa de excluir as EF Configurations" cobertura.sh 's#;-\*\*/Data/Configurations/\*\*##' casos_do_cobertura
  mutante "cobertura.sh: aviso de ExcludeFromCodeCoverage nao encontra mais o atributo" cobertura.sh 's#grep -rn "ExcludeFromCodeCoverage" src#grep -rn "ExcludeFromCodeCoverageNenhum" src#' casos_do_cobertura
  mutante "cobertura.sh: aviso de ExcludeFromCodeCoverage passa a listar obj/" cobertura.sh "s#| grep -v '/obj/' ##" casos_do_cobertura
  mutante "cobertura.sh: minimo padrao 80 -> 0"             cobertura.sh 's/COBERTURA_MINIMA:-80/COBERTURA_MINIMA:-0/' casos_do_cobertura
  mutante "cobertura.sh: fase desconhecida sai 0"           cobertura.sh '/Fase desconhecida/,/exit 2/ s/exit 2/exit 0/' casos_do_cobertura
  mutante "cobertura.sh: relatorio sem OpenCover aprova"    cobertura.sh '/nenhum coverage.opencover.xml/,/exit 2/ s/exit 2/exit 0/' casos_do_cobertura

  # ci.yml: o valor do gate de cobertura exportado pelo workflow.
  mutante_de_arquivo "ci.yml: gate de cobertura do CI reduzido de 80 para 10" .github/workflows/ci.yml 's/COBERTURA_MINIMA: "80"/COBERTURA_MINIMA: "10"/' casos_do_gate_no_workflow

  # exportar-swagger.sh, ci-local.sh e validar-k8s.sh.
  mutante "exportar-swagger.sh: --verificar nao falha quando o OpenAPI difere" exportar-swagger.sh '/esta desatualizado em relacao/,/^  exit 1$/ s/^  exit 1$/  exit 0/' casos_do_swagger
  mutante "exportar-swagger.sh: --verificar nao falha sem docs/openapi.json"   exportar-swagger.sh '/nao existe\. Rode/,/^  exit 1$/ s/^  exit 1$/  exit 0/' casos_do_swagger
  mutante "exportar-swagger.sh: \r\n das strings deixa de ser normalizado"    exportar-swagger.sh 's/ | sed .s\/\\\\r\\\\n\/\\\\n\/g.//' casos_do_swagger
  mutante "exportar-swagger.sh: argumento desconhecido sai 0"                  exportar-swagger.sh '/Argumento desconhecido/ s/exit 2/exit 0/' casos_do_swagger
  mutante "ci-local.sh: argumento desconhecido sai 0"       ci-local.sh '/Argumento desconhecido/ s/exit 2/exit 0/' casos_do_ci_local
  if [ "$TEM_DOCKER" = true ]; then
    mutante "validar-k8s.sh: kubeconform sem -strict"       validar-k8s.sh 's/-strict -summary/-summary/' casos_do_k8s
    mutante "validar-k8s.sh: CRD sem schema deixa de ser ignorado" validar-k8s.sh 's/ -ignore-missing-schemas//' casos_do_k8s
    mutante "validar-k8s.sh: falha do kubeconform e ignorada" validar-k8s.sh 's#-ignore-missing-schemas /manifests$#-ignore-missing-schemas /manifests || true#' casos_do_k8s
  else
    echo "  pulado  mutantes do validar-k8s.sh (Docker indisponivel)"
  fi
fi

echo
if [ "$FALHAS" -gt 0 ] || [ "$SOBREVIVENTES" -gt 0 ]; then
  echo "testar-scripts: $FALHAS caso(s) falharam de $CASOS; $SOBREVIVENTES mutante(s) sobreviveram/invalidos de $MUTANTES." >&2
  exit 1
fi
echo "testar-scripts OK: $CASOS casos e $MUTANTES mutantes detectados."
