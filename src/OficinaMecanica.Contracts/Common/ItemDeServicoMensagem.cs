namespace OficinaMecanica.Contracts.Common;

public sealed record ItemDeServicoMensagem(Guid ServicoId, string Nome, decimal PrecoUnitario, int Quantidade);
