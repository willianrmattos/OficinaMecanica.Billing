#!/usr/bin/env bash
# Mede a cobertura de linhas do servico inteiro: roda todos os projetos de teste (inclusive os testes de infra, que
# usam Docker) com o coverlet em formato OpenCover e une os relatorios com o ReportGenerator.
#
# Saidas em TestResults/: <projeto>/**/coverage.opencover.xml e <projeto>/*.trx, report-bruto/ e report-com-exclusoes/
# (Summary.txt, Html e SummaryGithub.md) e resumo-cobertura.md. "Bruto" conta tudo; "com exclusoes" tira so o que o
# CLAUDE.md documenta (Migrations, Program.cs, DependencyInjection* e as EF Configurations).
#
# Uso: bash scripts/cobertura.sh [fase]
#   (sem argumento)    limpa TestResults e roda tudo: testes sem Infra, testes de Infra, relatorio e gate
#   testes-sem-infra   contracts, domain, application e integracao com Category!=Infra
#   testes-infra       integracao com Category=Infra (Testcontainers: precisa de Docker)
#   relatorio          une os coverage.opencover.xml ja gerados, escreve os relatorios e aplica o gate
# O CI chama cada fase em um step proprio para o resultado de cada grupo aparecer separado; os testes rodam uma unica
# vez, ja com a coleta de cobertura, e o resultado e o mesmo do modo completo.
#
# Variaveis:
#   COBERTURA_MINIMA=80        limite do gate (% de linhas); o gate vale para o numero "com exclusoes"
#   COBERTURA_SEM_BUILD=true   usa --no-build/--no-restore (o CI ja compilou em Release antes)
set -euo pipefail

cd "$(dirname "$0")/.."

FASE="${1:-tudo}"
export COBERTURA_MINIMA="${COBERTURA_MINIMA:-80}"

ARGS_BUILD=()
if [ "${COBERTURA_SEM_BUILD:-false}" = "true" ]; then
  ARGS_BUILD=(--no-build --no-restore)
fi

# Optei por --blame-hang-timeout (por teste, sem despejo de memoria): um teste que espera uma tarefa que o codigo sob teste deixou
# de liberar trava o run em silencio, e sem o limite so o timeout-minutes do job o encerraria (e o ci-local nao tem nenhum).
# Com ele o testhost e encerrado e o teste aparece como falha.
executar() {
  local nome="$1" projeto="$2"; shift 2
  dotnet test "$projeto" -c Release ${ARGS_BUILD[@]+"${ARGS_BUILD[@]}"} --settings coverage.runsettings \
    --collect:"XPlat Code Coverage" --results-directory "TestResults/$nome" \
    --logger "trx;LogFileName=$nome.trx" --blame-hang-timeout 5m --blame-hang-dump-type none "$@"
}

testes_sem_infra() {
  executar contracts   tests/OficinaMecanica.Contracts.Tests
  executar domain      tests/OficinaMecanica.Billing.Domain.Tests
  executar application tests/OficinaMecanica.Billing.Application.Tests
  executar integracao  tests/OficinaMecanica.Billing.Integration.Tests --filter "Category!=Infra"
}

testes_infra() {
  executar infra tests/OficinaMecanica.Billing.Integration.Tests --filter "Category=Infra"
}

linha_de_cobertura() {
  grep -E "Line coverage" "$1" | head -n 1 | tr -d '\r' | sed -E 's/^[[:space:]]+//'
}

relatorio() {
  dotnet tool restore > /dev/null

  local relatorios="TestResults/**/coverage.opencover.xml"
  if ! compgen -G "TestResults/*/*/coverage.opencover.xml" > /dev/null; then
    echo "ERRO: nenhum coverage.opencover.xml em TestResults/; rode as fases de teste antes do relatorio." >&2
    exit 2
  fi

  dotnet reportgenerator "-reports:$relatorios" "-targetdir:TestResults/report-bruto" \
    "-reporttypes:TextSummary;Html" "-assemblyfilters:+OficinaMecanica.*"
  dotnet reportgenerator "-reports:$relatorios" "-targetdir:TestResults/report-com-exclusoes" \
    "-reporttypes:TextSummary;Html;MarkdownSummaryGithub" "-assemblyfilters:+OficinaMecanica.*" \
    "-filefilters:-**/Migrations/**;-**/Program.cs;-**/DependencyInjection*.cs;-**/Data/Configurations/**"

  local bruta com_exclusoes
  bruta="$(linha_de_cobertura TestResults/report-bruto/Summary.txt)"
  com_exclusoes="$(linha_de_cobertura TestResults/report-com-exclusoes/Summary.txt)"
  echo; echo "=== Cobertura de linhas (bruta) ===";         echo "$bruta"
  echo "=== Cobertura de linhas (com exclusoes) ==="; echo "$com_exclusoes"

  # O coverlet honra [ExcludeFromCodeCoverage] (coverage.runsettings) e o codigo marcado some, em silencio, das DUAS medicoes
  # acima. O gate nao tem como julgar a justificativa, entao a contagem entra no relatorio: o uso so e aceito com ADR.
  local atributos
  atributos="$(grep -rn "ExcludeFromCodeCoverage" src --include='*.cs' 2>/dev/null | grep -v '/obj/' | grep -v '/bin/' || true)"
  if [ -n "$atributos" ]; then
    echo "AVISO: [ExcludeFromCodeCoverage] em src/ (some das duas medicoes; exige justificativa em ADR):"
    printf '%s\n' "$atributos" | sed 's/^/  /'
  fi

  # Resumo em Markdown para o Job Summary do GitHub Actions (o workflow so anexa o arquivo).
  {
    echo "## Cobertura de linhas"
    echo
    echo "| Medicao | Resultado |"
    echo "|---|---|"
    echo "| Bruta (conta tudo) | ${bruta#Line coverage: } |"
    echo "| Com exclusoes (valida o gate de ${COBERTURA_MINIMA}%) | ${com_exclusoes#Line coverage: } |"
    echo "| Ocorrencias de [ExcludeFromCodeCoverage] em src/ | $(printf '%s' "$atributos" | grep -c . || true) |"
    echo
    cat TestResults/report-com-exclusoes/SummaryGithub.md
  } > TestResults/resumo-cobertura.md

  bash scripts/verificar-cobertura.sh TestResults/report-com-exclusoes/Summary.txt "$COBERTURA_MINIMA"
}

case "$FASE" in
  tudo)
    dotnet tool restore > /dev/null
    rm -rf TestResults
    testes_sem_infra
    testes_infra
    relatorio
    ;;
  testes-sem-infra) testes_sem_infra ;;
  testes-infra)     testes_infra ;;
  relatorio)        relatorio ;;
  *)
    echo "Fase desconhecida: '$FASE'. Use: tudo | testes-sem-infra | testes-infra | relatorio." >&2
    exit 2
    ;;
esac
