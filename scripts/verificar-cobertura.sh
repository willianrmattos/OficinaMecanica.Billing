#!/usr/bin/env bash
# Gate de cobertura: calcula a cobertura de linhas de um Summary.txt do ReportGenerator e falha (exit 1) se ficar abaixo do
# minimo. Separado do scripts/cobertura.sh para poder ser exercitado sozinho com um resumo sintetico (scripts/testar-scripts.sh).
#
# Uso: bash scripts/verificar-cobertura.sh <Summary.txt> [minimo]
# O minimo vem do 2o argumento, de COBERTURA_MINIMA ou, na falta (ou vazio) dos dois, 80 (meta do ecossistema).
# Saidas: 0 aprovado, 1 abaixo do minimo, 2 entrada invalida (arquivo, minimo ou contagem de linhas ausente).
#
# O percentual vem das linhas cobertas e cobertaveis ("Covered lines" e "Coverable lines"), nao da linha "Line coverage":
# o ReportGenerator arredonda essa para uma casa decimal e 79,95% apareceria como 80% e passaria no gate.
#
# Atencao: o atributo [ExcludeFromCodeCoverage] e honrado pelo coverlet (coverage.runsettings) e faz o codigo sumir das duas
# medicoes (bruta e com exclusoes) sem deixar rastro neste gate; por isso o CLAUDE.md o proibe sem justificativa em ADR.
set -euo pipefail

RESUMO="${1:?uso: verificar-cobertura.sh <Summary.txt> [minimo]}"
MINIMO="${2:-${COBERTURA_MINIMA:-80}}"

if [ ! -f "$RESUMO" ]; then
  echo "ERRO: resumo de cobertura nao encontrado: $RESUMO" >&2
  exit 2
fi

if ! printf '%s' "$MINIMO" | grep -Eq '^[0-9]+([.][0-9]+)?$'; then
  echo "ERRO: COBERTURA_MINIMA invalida ('$MINIMO'); use um numero, por exemplo 80." >&2
  exit 2
fi

# "Uncovered lines" nao casa: o rotulo e comparado com a capital C e ancorado no inicio da linha.
lerContagem() {
  grep -E "^[[:space:]]*$1:" "$RESUMO" | head -n 1 | tr -d '\r' | sed -nE "s/^[[:space:]]*$1:[[:space:]]*([0-9]+).*/\\1/p" || true
}

COBERTAS="$(lerContagem 'Covered lines')"
COBERTAVEIS="$(lerContagem 'Coverable lines')"

if [ -z "$COBERTAS" ] || [ -z "$COBERTAVEIS" ]; then
  echo "ERRO: nao achei 'Covered lines' e 'Coverable lines' em $RESUMO; sem a contagem de linhas o gate nao pode aprovar." >&2
  exit 2
fi

if [ "$COBERTAVEIS" -eq 0 ]; then
  echo "ERRO: 'Coverable lines' e zero em $RESUMO; nao ha linhas medidas para o gate avaliar." >&2
  exit 2
fi

PERCENTUAL="$(LC_ALL=C awk -v c="$COBERTAS" -v t="$COBERTAVEIS" 'BEGIN { printf "%.2f", c * 100 / t }')"

# Comparacao sem arredondar: cobertas * 100 >= minimo * cobertaveis.
if LC_ALL=C awk -v c="$COBERTAS" -v t="$COBERTAVEIS" -v m="$MINIMO" 'BEGIN { exit !(c * 100 >= m * t) }'; then
  echo "Gate de cobertura OK: ${PERCENTUAL}% de linhas (${COBERTAS} de ${COBERTAVEIS}; minimo ${MINIMO}%)."
else
  echo "Gate de cobertura REPROVADO: ${PERCENTUAL}% de linhas (${COBERTAS} de ${COBERTAVEIS}) esta abaixo do minimo de ${MINIMO}%." >&2
  exit 1
fi
