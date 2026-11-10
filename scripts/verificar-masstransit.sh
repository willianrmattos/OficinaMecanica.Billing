#!/usr/bin/env bash
# Guardrail de licenca: o MassTransit 9.x e comercial, e o 8.x (gratuito) e a unica linha permitida no ecossistema.
# Falha se QUALQUER pacote MassTransit* de QUALQUER projeto resolver para um major diferente do permitido, seja
# dependencia direta ou transitiva (um pacote de terceiros pode arrastar o MassTransit 9 sem aparecer em nenhum .csproj).
#
# Uso: bash scripts/verificar-masstransit.sh [solucao-ou-projeto]     (padrao: a .sln da raiz)
#      bash scripts/verificar-masstransit.sh --entrada <arquivo>      (analisa uma saida ja capturada de
#                                                                      'dotnet list package --include-transitive')
# Variavel: MASSTRANSIT_MAJOR_PERMITIDO (padrao 8)
# Saidas: 0 ok, 1 major nao permitido, 2 nao foi possivel listar os pacotes, 3 nenhum MassTransit* encontrado.
# Roda depois do restore (le o project.assets.json de cada projeto); o CI e o ci-local seguem essa ordem.
# O --entrada existe para scripts/testar-scripts.sh provar o parse com uma saida sintetica, sem depender de restore.
set -euo pipefail

cd "$(dirname "$0")/.."

ENTRADA=""
ALVO=""
while [ $# -gt 0 ]; do
  case "$1" in
    --entrada) ENTRADA="${2:?uso: --entrada <arquivo>}"; shift 2 ;;
    *) ALVO="$1"; shift ;;
  esac
done
MAJOR_PERMITIDO="${MASSTRANSIT_MAJOR_PERMITIDO:-8}"

# A saida do dotnet segue o idioma do sistema; o filtro abaixo nao depende do texto, mas o ingles mantem o log legivel.
export DOTNET_CLI_UI_LANGUAGE=en

if [ -n "$ENTRADA" ]; then
  if [ ! -f "$ENTRADA" ]; then
    echo "ERRO: arquivo de entrada nao encontrado: $ENTRADA" >&2
    exit 2
  fi
  ALVO="$ENTRADA"
  SAIDA="$(cat "$ENTRADA")"
else
  if [ -z "$ALVO" ]; then
    shopt -s nullglob
    SOLUCOES=(./*.sln)
    ALVO="${SOLUCOES[0]:-}"
  fi
  if [ -z "$ALVO" ]; then
    echo "ERRO: nenhuma .sln na raiz e nenhum alvo informado (uso: verificar-masstransit.sh [solucao-ou-projeto])." >&2
    exit 2
  fi
  if ! SAIDA=$(dotnet list "$ALVO" package --include-transitive 2>&1); then
    printf '%s\n' "$SAIDA"
    echo "ERRO: 'dotnet list package' falhou; o guardrail nao pode afirmar nada sobre as versoes do MassTransit." >&2
    exit 2
  fi
fi

# O formato do 'dotnet list package' e: uma linha sem indentacao com o nome do projeto entre aspas simples, seguida de
# linhas "   > Pacote  [requisitado]  resolvido". A versao resolvida e sempre o ultimo campo (diretas e transitivas).
PACOTES=$(printf '%s\n' "$SAIDA" | tr -d '\r' | awk -v aspa="'" '
  /^[^ >].*\x27[^\x27]+\x27/ { split($0, partes, aspa); projeto = partes[2] }
  $1 == ">" && $2 ~ /^MassTransit/ { print projeto, $2, $NF }
' | sort -u)

if [ -z "$PACOTES" ]; then
  echo "ERRO: nenhum pacote MassTransit* encontrado em $ALVO; o guardrail ficaria vazio (restore nao feito?)." >&2
  exit 3
fi

echo "Pacotes MassTransit* resolvidos (projeto pacote versao):"
printf '%s\n' "$PACOTES" | sed 's/^/  /'

VIOLACOES=$(printf '%s\n' "$PACOTES" | awk -v permitido="$MAJOR_PERMITIDO" '{ split($3, v, "."); if (v[1] != permitido) print "  " $1 " " $2 " " $3 }')

if [ -n "$VIOLACOES" ]; then
  echo >&2
  echo "ERRO: pacote(s) MassTransit* fora do major $MAJOR_PERMITIDO (o 9.x e comercial):" >&2
  printf '%s\n' "$VIOLACOES" >&2
  echo "Fixe a versao 8.5.12 exata no .csproj do projeto citado ou remova o pacote que arrasta o MassTransit 9." >&2
  exit 1
fi

echo "OK: todos os pacotes MassTransit* resolvem para o major $MAJOR_PERMITIDO."
