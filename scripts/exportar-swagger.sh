#!/usr/bin/env bash
# Exporta o OpenAPI do servico com a ferramenta 'swagger tofile' (Swashbuckle.AspNetCore.Cli, fixada em
# dotnet-tools.json na mesma versao do Swashbuckle da API) e garante que docs/openapi.json versionado reflita a API.
#
# Uso:
#   bash scripts/exportar-swagger.sh               regera docs/openapi.json (rode e versione quando a API mudar)
#   bash scripts/exportar-swagger.sh --verificar   gera em artifacts/openapi.json e FALHA se docs/openapi.json difere
#   --sem-build                                    reaproveita o build Release existente (o CI ja compilou)
#
# Para clonar em outro servico basta trocar as tres variaveis abaixo.
set -euo pipefail

cd "$(dirname "$0")/.."

API_PROJETO="src/OficinaMecanica.Billing.API/OficinaMecanica.Billing.API.csproj"
API_DLL="src/OficinaMecanica.Billing.API/bin/Release/net8.0/OficinaMecanica.Billing.API.dll"
DOCUMENTO="v1"

VERSIONADO="docs/openapi.json"
GERADO="artifacts/openapi.json"

VERIFICAR=false
COMPILAR=true
for argumento in "$@"; do
  case "$argumento" in
    --verificar) VERIFICAR=true ;;
    --sem-build) COMPILAR=false ;;
    *) echo "Argumento desconhecido: $argumento (use --verificar e/ou --sem-build)" >&2; exit 2 ;;
  esac
done

dotnet tool restore > /dev/null
if [ "$COMPILAR" = true ]; then
  dotnet build "$API_PROJETO" -c Release
fi

gerar() {
  local destino="$1" temporario
  mkdir -p "$(dirname "$destino")"
  temporario="$(mktemp)"

  # O 'swagger tofile' executa o Program.Main e INICIA o host (so nao abre a porta), entao a validacao de opcoes da
  # partida roda. Optei por Testing + transporte em memoria + sem outbox + reconciliador desligado para a partida nao
  # exigir SQL nem RabbitMQ, e por valores de fachada nas credenciais do Mercado Pago (so precisam passar na validacao;
  # nenhum segredo real). Testing e Production geram o mesmo documento: o controller de simulacao de pagamento so
  # existe em Development.
  env ASPNETCORE_ENVIRONMENT=Testing \
      Mensageria__Transporte=EmMemoria \
      Mensageria__Persistencia=Nenhuma \
      Billing__ReconciliadorHabilitado=false \
      Pagamento__Provider=MercadoPago \
      MercadoPago__AccessToken=fachada \
      MercadoPago__WebhookSecret=fachada \
      MercadoPago__NotificationBaseUrl=https://exemplo.invalid \
      dotnet swagger tofile --output "$temporario" "$API_DLL" "$DOCUMENTO" > /dev/null

  # Final de linha sempre LF, em dois niveis: o gerador escreve CRLF no Windows (arquivo), e o compilador junta as linhas
  # dos comentarios XML multilinha com o separador da plataforma (ou com o final de linha do .cs de quem fez o checkout),
  # o que deixa "\r\n" DENTRO das strings do JSON. Sem normalizar os dois, o arquivo versionado dependeria de onde foi
  # gerado e o CI (Linux, checkout em LF) reprovaria o que saiu do Windows.
  tr -d '\r' < "$temporario" | sed 's/\\r\\n/\\n/g' > "$destino"
  [ -n "$(tail -c 1 "$destino")" ] && echo >> "$destino"
  rm -f "$temporario"
}

if [ "$VERIFICAR" = false ]; then
  gerar "$VERSIONADO"
  echo "OpenAPI exportado para $VERSIONADO"
  exit 0
fi

gerar "$GERADO"
echo "OpenAPI gerado em $GERADO"

if [ ! -f "$VERSIONADO" ]; then
  echo "ERRO: $VERSIONADO nao existe. Rode 'bash scripts/exportar-swagger.sh' e versione o arquivo." >&2
  exit 1
fi

if ! diff -u --strip-trailing-cr "$VERSIONADO" "$GERADO" > artifacts/openapi.diff; then
  echo "ERRO: $VERSIONADO esta desatualizado em relacao a API. Diferencas (versionado -> gerado):" >&2
  head -n 60 artifacts/openapi.diff >&2
  echo "Rode 'bash scripts/exportar-swagger.sh' e versione o resultado." >&2
  exit 1
fi

echo "OK: $VERSIONADO esta em dia com a API."
