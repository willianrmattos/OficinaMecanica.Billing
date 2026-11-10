#!/usr/bin/env bash
# Guarda a separacao de privilegios do ci.yml: um job que declara id-token (pode pedir o token OIDC do Azure, e a identidade
# do servico tem AcrPush, AKS Cluster Admin e leitura de secret) so pode executar actions do GitHub (actions/*) e da
# Microsoft (azure/*). As actions de terceiros (docker/*, aquasecurity/*, dorny/*...) ficam em jobs sem id-token, como o
# scan-image. Tambem falha se id-token for concedido no nivel do workflow (valeria para todos os jobs).
#
# Uso: bash scripts/verificar-workflow.sh [ci.yml]      (padrao: .github/workflows/ci.yml)
# Saidas: 0 ok, 1 regra violada, 2 arquivo invalido ou sem jobs. Os comentarios do YAML sao ignorados. O parse e textual
# (jobs na indentacao de 2 espacos), suficiente para o formato do ci.yml; scripts/testar-scripts.sh prova os casos de falha.
set -euo pipefail

if [ $# -ge 1 ]; then
  ARQUIVO="$1"
else
  cd "$(dirname "$0")/.."
  ARQUIVO=".github/workflows/ci.yml"
fi

if [ ! -f "$ARQUIVO" ]; then
  echo "ERRO: workflow nao encontrado: $ARQUIVO" >&2
  exit 2
fi

# Linhas do workflow que nao sao comentario.
SEM_COMENTARIOS="$(tr -d '\r' < "$ARQUIVO" | grep -Ev '^[[:space:]]*#' || true)"

# Tudo antes de "jobs:" (permissions do workflow inteiro).
NIVEL_DO_WORKFLOW="$(printf '%s\n' "$SEM_COMENTARIOS" | awk '/^jobs:/ { exit } { print }')"

IDS="$(printf '%s\n' "$SEM_COMENTARIOS" | awk '
  /^jobs:/ { dentro = 1; next }
  /^[^ ]/ { dentro = 0 }
  dentro && /^  [A-Za-z0-9_-]+:[[:space:]]*$/ { split($1, partes, ":"); print partes[1] }')"

if [ -z "$IDS" ]; then
  echo "ERRO: nenhum job encontrado em $ARQUIVO." >&2
  exit 2
fi

VIOLACOES=""

if printf '%s\n' "$NIVEL_DO_WORKFLOW" | grep -Eq 'id-token|write-all'; then
  VIOLACOES="${VIOLACOES}  workflow: concede id-token (ou write-all) a TODOS os jobs; declare so nos jobs que fazem login no Azure\n"
fi

# Linhas do job (do cabecalho ate o proximo job ou o fim).
bloco_do_job() {
  printf '%s\n' "$SEM_COMENTARIOS" | awk -v job="$1" '
    /^jobs:/ { dentro = 1; achou = 0; next }
    /^[^ ]/ { dentro = 0; achou = 0 }
    dentro && /^  [A-Za-z0-9_-]+:[[:space:]]*$/ { split($1, partes, ":"); achou = (partes[1] == job) }
    dentro && achou { print }'
}

for id in $IDS; do
  bloco="$(bloco_do_job "$id")"
  printf '%s\n' "$bloco" | grep -Eq 'id-token|write-all' || continue

  # Valor de cada "uses:" do job, sem a versao (@...), as aspas e o "- " da lista.
  usos="$(printf '%s\n' "$bloco" | awk -v aspa="'" '
    /^[[:space:]]*-?[[:space:]]*uses:/ {
      sub(/^[[:space:]]*-?[[:space:]]*uses:[[:space:]]*/, "")
      gsub(aspa, ""); gsub(/"/, "")
      split($0, partes, "[@[:space:]]")
      print partes[1]
    }')"
  proibidas="$(printf '%s\n' "$usos" | grep -Ev '^(actions|azure)/' | grep -v '^$' || true)"
  if [ -n "$proibidas" ]; then
    VIOLACOES="${VIOLACOES}  job '$id' declara id-token e executa action fora de actions/* e azure/*: $(printf '%s' "$proibidas" | tr '\n' ' ')\n"
  fi
done

if [ -n "$VIOLACOES" ]; then
  echo "ERRO: separacao de privilegios do $ARQUIVO violada:" >&2
  printf '%b' "$VIOLACOES" >&2
  echo "Mova a action de terceiros para um job sem id-token (como o scan-image) ou remova o id-token do job." >&2
  exit 1
fi

echo "OK: nenhum job com id-token executa action de terceiros ($ARQUIVO)."
