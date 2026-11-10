using FluentAssertions;
using OficinaMecanica.Billing.Application.Queries.ListarOrcamentos;

namespace OficinaMecanica.Billing.Application.Tests.Validators;

public class ListarOrcamentosQueryValidatorTests
{
    private readonly ListarOrcamentosQueryValidator _validator = new();

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 10)]
    [InlineData(50, 100)]
    public async Task Validate_ComPaginacaoValida_DeveSerValido(int pagina, int tamanho)
    {
        var result = await _validator.ValidateAsync(new ListarOrcamentosQuery(pagina, tamanho));

        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Validate_ComPaginaMenorQueUm_DeveSerInvalido(int pagina)
    {
        var result = await _validator.ValidateAsync(new ListarOrcamentosQuery(pagina, 10));

        result.Errors.Should().Contain(e => e.PropertyName == "Pagina" && e.ErrorMessage == "A página deve ser maior ou igual a 1.");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task Validate_ComTamanhoDePaginaForaDoLimite_DeveSerInvalido(int tamanho)
    {
        var result = await _validator.ValidateAsync(new ListarOrcamentosQuery(1, tamanho));

        result.Errors.Should().Contain(e => e.PropertyName == "TamanhoPagina" && e.ErrorMessage == "O tamanho da página deve estar entre 1 e 100.");
    }
}
