#!/usr/bin/env bash
# Mede a cobertura de linhas do servico inteiro: roda todos os projetos de teste (inclusive os testes de infra, que
# usam Docker) com o coverlet em formato OpenCover e une os relatorios com o ReportGenerator.
#
# Saidas em TestResults/: <projeto>/**/coverage.opencover.xml, report-bruto/ e report-com-exclusoes/ (Summary.txt e Html).
# "Bruto" conta tudo; "com exclusoes" tira so o que o CLAUDE.md documenta (Migrations, Program.cs, DependencyInjection*
# e as EF Configurations).
set -euo pipefail

cd "$(dirname "$0")/.."
dotnet tool restore > /dev/null
rm -rf TestResults

executar() {
  local nome="$1" projeto="$2"; shift 2
  dotnet test "$projeto" -c Release --settings coverage.runsettings \
    --collect:"XPlat Code Coverage" --results-directory "TestResults/$nome" "$@"
}

executar contracts   tests/OficinaMecanica.Contracts.Tests
executar domain      tests/OficinaMecanica.Billing.Domain.Tests
executar application tests/OficinaMecanica.Billing.Application.Tests
executar integracao  tests/OficinaMecanica.Billing.Integration.Tests --filter "Category!=Infra"
executar infra       tests/OficinaMecanica.Billing.Integration.Tests --filter "Category=Infra"

RELATORIOS="TestResults/**/coverage.opencover.xml"
dotnet reportgenerator "-reports:$RELATORIOS" "-targetdir:TestResults/report-bruto" \
  "-reporttypes:TextSummary;Html" "-assemblyfilters:+OficinaMecanica.*"
dotnet reportgenerator "-reports:$RELATORIOS" "-targetdir:TestResults/report-com-exclusoes" \
  "-reporttypes:TextSummary;Html" "-assemblyfilters:+OficinaMecanica.*" \
  "-filefilters:-**/Migrations/**;-**/Program.cs;-**/DependencyInjection*.cs;-**/Data/Configurations/**"

echo; echo "=== Cobertura de linhas (bruta) ===";         grep -E "Line coverage" TestResults/report-bruto/Summary.txt
echo "=== Cobertura de linhas (com exclusoes) ==="; grep -E "Line coverage" TestResults/report-com-exclusoes/Summary.txt
