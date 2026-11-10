## O que muda e por que

<!-- Uma ou duas frases. Link para a issue/ADR, se houver. -->

## Checklist

- [ ] Testes novos ou ajustados; `bash scripts/ci-local.sh` passou (ou o motivo de nao ter rodado esta descrito abaixo)
- [ ] Cobertura de linhas continua >= 80% (gate do CI)
- [ ] Contratos de mensagem (`OficinaMecanica.Contracts`) e goldens JSON iguais aos dos outros repositorios; evolucao so aditiva
- [ ] `docs/openapi.json` regenerado (`bash scripts/exportar-swagger.sh`) se a API mudou
- [ ] ADR em `docs/adr/` quando ha decisao de arquitetura nova
- [ ] Nenhum segredo, token ou connection string real no diff (segredos so no Key Vault / variaveis do GitHub)
