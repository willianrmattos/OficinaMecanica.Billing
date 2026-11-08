#!/usr/bin/env bash
# Cria ou atualiza no GitHub o ruleset "protect-main-release" (arquivo versionado em
# .github/rulesets/protect-main-release.json): main e release sem delete nem force-push, PR obrigatorio e os checks
# "Build & Test" e "Quality" exigidos.
#
# SO RODA NA PUBLICACAO: exige o repositorio ja criado no GitHub, a GitHub CLI (gh) autenticada com permissao de
# administracao no repositorio e o CI ja ter rodado ao menos uma vez (o GitHub so oferece como obrigatorio um check que
# ja apareceu). Nada neste repositorio executa este script automaticamente.
#
# Uso: bash scripts/aplicar-ruleset.sh [owner/repo] [--dry-run]
#   owner/repo   padrao: willianrmattos/OficinaMecanica.Billing
#   --dry-run    so mostra o que faria (nao chama o GitHub)
#
# Idempotente: se ja existir um ruleset com o mesmo nome, atualiza (PUT) em vez de criar outro.
set -euo pipefail

cd "$(dirname "$0")/.."

REPO="willianrmattos/OficinaMecanica.Billing"
DRY_RUN=false
for argumento in "$@"; do
  case "$argumento" in
    --dry-run) DRY_RUN=true ;;
    */*) REPO="$argumento" ;;
    *) echo "Argumento desconhecido: $argumento (use owner/repo e/ou --dry-run)" >&2; exit 2 ;;
  esac
done

ARQUIVO=".github/rulesets/protect-main-release.json"
NOME="protect-main-release"

if [ ! -f "$ARQUIVO" ]; then
  echo "ERRO: $ARQUIVO nao encontrado." >&2
  exit 1
fi

if [ "$DRY_RUN" = true ]; then
  echo "[dry-run] procuraria o ruleset '$NOME' em $REPO e o criaria/atualizaria com $ARQUIVO:"
  echo "[dry-run]   gh api repos/$REPO/rulesets            (lista)"
  echo "[dry-run]   gh api --method POST|PUT repos/$REPO/rulesets[/<id>] --input $ARQUIVO"
  exit 0
fi

if ! command -v gh > /dev/null; then
  echo "ERRO: GitHub CLI (gh) nao encontrada." >&2
  exit 1
fi

ID="$(gh api "repos/$REPO/rulesets" --jq ".[] | select(.name == \"$NOME\") | .id")"

if [ -n "$ID" ]; then
  echo "Ruleset '$NOME' ja existe em $REPO (id $ID): atualizando."
  gh api --method PUT "repos/$REPO/rulesets/$ID" --input "$ARQUIVO" --jq '"atualizado: id \(.id), enforcement \(.enforcement)"'
else
  echo "Ruleset '$NOME' nao existe em $REPO: criando."
  gh api --method POST "repos/$REPO/rulesets" --input "$ARQUIVO" --jq '"criado: id \(.id), enforcement \(.enforcement)"'
fi
