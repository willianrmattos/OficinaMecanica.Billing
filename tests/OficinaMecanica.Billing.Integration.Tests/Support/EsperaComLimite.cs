using FluentAssertions.Execution;

namespace OficinaMecanica.Billing.Integration.Tests.Support;

// Optei por limitar toda espera de teste que depende de uma tarefa que o código sob teste poderia deixar de liberar (um
// TaskCompletionSource, uma avaliação em andamento). Sem o limite, remover a proteção que o teste exercita faz o teste
// travar em vez de reprovar, e o run só terminaria no timeout do job. Com ele, a falta da proteção vira uma falha rápida
// com a explicação do que deveria ter acontecido.
public static class EsperaComLimite
{
    // Acima de qualquer espera legítima dos testes (o maior limite do código sob teste é de 3 s) e curto o bastante para a
    // falha aparecer rápido.
    public static readonly TimeSpan Padrao = TimeSpan.FromSeconds(10);

    public static async Task AguardarAsync(this Task tarefa, string porque, TimeSpan? limite = null)
    {
        var espera = limite ?? Padrao;
        var primeira = await Task.WhenAny(tarefa, Task.Delay(espera));
        if (!ReferenceEquals(primeira, tarefa))
            Execute.Assertion.FailWith("Esperava que a tarefa terminasse em {0}, mas ela continua pendente: {1}", espera, porque);

        await tarefa;
    }

    public static async Task<T> AguardarAsync<T>(this Task<T> tarefa, string porque, TimeSpan? limite = null)
    {
        await ((Task)tarefa).AguardarAsync(porque, limite);
        return await tarefa;
    }
}
