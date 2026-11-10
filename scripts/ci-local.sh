#!/usr/bin/env bash
# Roda na maquina do desenvolvedor os mesmos passos do job "Build & Test" do .github/workflows/ci.yml, na mesma ordem
# e chamando os mesmos scripts, para depurar uma falha de CI sem dar push.
# Os jobs quality (SonarCloud), build-and-push-image e deploy-to-aks dependem de secrets/Azure e nao rodam aqui.
#
# Uso: bash scripts/ci-local.sh [--sem-infra]
#   --sem-infra   pula os testes de Infra (Testcontainers) para quem nao tem Docker; sem eles a cobertura fica
#                 incompleta, entao o gate de cobertura NAO e avaliado (o resultado so vale para "Build & Test" parcial)
#
# Variavel: COBERTURA_MINIMA (padrao 80), a mesma do workflow.
set -euo pipefail

cd "$(dirname "$0")/.."

SOLUTION="OficinaMecanica.Billing.sln"
SEM_INFRA=false
for argumento in "$@"; do
  case "$argumento" in
    --sem-infra) SEM_INFRA=true ;;
    *) echo "Argumento desconhecido: $argumento (use --sem-infra)" >&2; exit 2 ;;
  esac
done

export COBERTURA_MINIMA="${COBERTURA_MINIMA:-80}"
# O build Release abaixo e reaproveitado pelos scripts de teste e de OpenAPI, como no workflow.
export COBERTURA_SEM_BUILD=true

passo() { printf '\n=== %s ===\n' "$*"; }

TEM_DOCKER=false
if docker info > /dev/null 2>&1; then
  TEM_DOCKER=true
fi

if [ "$SEM_INFRA" = false ] && [ "$TEM_DOCKER" = false ]; then
  echo "ERRO: Docker indisponivel; os testes de Infra (Testcontainers) precisam dele. Use --sem-infra para pular." >&2
  exit 1
fi

passo "Restore";                dotnet restore "$SOLUTION"
passo "Build (Release)";        dotnet build "$SOLUTION" --no-restore --configuration Release
passo "Testes dos scripts de CI (gate, guardrail e mutantes)"
bash scripts/testar-scripts.sh
passo "Guardrail MassTransit";  bash scripts/verificar-masstransit.sh "$SOLUTION"

passo "Manifests do Kubernetes (kubeconform)"
if [ "$TEM_DOCKER" = true ]; then
  bash scripts/validar-k8s.sh
else
  echo "AVISO: Docker indisponivel, manifests nao validados."
fi

passo "OpenAPI (docs/openapi.json em dia)"
bash scripts/exportar-swagger.sh --verificar --sem-build

rm -rf TestResults
passo "Testes (Category!=Infra)"
bash scripts/cobertura.sh testes-sem-infra

if [ "$SEM_INFRA" = true ]; then
  echo
  echo "AVISO: testes de Infra e gate de cobertura (minimo ${COBERTURA_MINIMA}%) NAO executados (--sem-infra)."
  exit 0
fi

passo "Testes (Category=Infra, Testcontainers)"
bash scripts/cobertura.sh testes-infra

passo "Cobertura mesclada e gate (minimo ${COBERTURA_MINIMA}%)"
bash scripts/cobertura.sh relatorio

echo
echo "ci-local OK: mesmos passos do job Build & Test concluidos. Relatorio HTML em TestResults/report-com-exclusoes/index.html"
