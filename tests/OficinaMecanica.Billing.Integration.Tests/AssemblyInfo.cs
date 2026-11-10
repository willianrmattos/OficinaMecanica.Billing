using Xunit;

// Os testes compartilham contadores de Meter, variáveis de ambiente e containers: rodar em série evita medições
// de um teste vazando para outro.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
