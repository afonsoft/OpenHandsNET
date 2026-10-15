namespace OpenHandsNET.Client.Services;

/// <summary>
/// SPEC-26 RF-004: estado da command palette (Ctrl+K / "/").
/// Os itens são registrados por quem os provê (sidebar, layout, páginas)
/// e filtrados por fuzzy match (subsequence case-insensitive).
/// </summary>
public sealed class PaletteService
{
    /// <summary>Item acionável da palette.</summary>
    public sealed record Item(
        string Id, string Label, string? Hint, string Icon,
        Func<Task> Action);

    /// <summary>Palette está aberta.</summary>
    public bool IsOpen { get; private set; }

    /// <summary>Itens filtrados pelo query corrente.</summary>
    public IReadOnlyList<Item> Filtered { get; private set; } = [];

    /// <summary>Índice selecionado (navegação por setas).</summary>
    public int Selected { get; private set; }

    /// <summary>Dispara quando estado/lista muda.</summary>
    public event Action? Changed;

    private List<Item> _items = [];

    /// <summary>Fonte de itens — reatualizada a cada abertura.</summary>
    public Func<Task<IEnumerable<Item>>>? Provider { get; set; }

    /// <summary>Abre a palette e recarrega os itens do provider.</summary>
    public async Task OpenAsync()
    {
        _items = Provider is not null
            ? (await Provider()).ToList() : _items;
        ApplyFilter("");
        IsOpen = true;
        Selected = 0;
        Changed?.Invoke();
    }

    /// <summary>Fecha a palette.</summary>
    public void Close()
    {
        IsOpen = false;
        Changed?.Invoke();
    }

    /// <summary>Atualiza o query e refiltra.</summary>
    public void SetQuery(string query)
    {
        ApplyFilter(query);
        Selected = 0;
        Changed?.Invoke();
    }

    /// <summary>Navega (+1/-1) na lista filtrada.</summary>
    public void Move(int delta)
    {
        if (Filtered.Count == 0)
        {
            return;
        }

        Selected = (Selected + delta + Filtered.Count) % Filtered.Count;
        Changed?.Invoke();
    }

    /// <summary>Executa o item selecionado e fecha.</summary>
    public async Task InvokeSelectedAsync()
    {
        if (Selected >= 0 && Selected < Filtered.Count)
        {
            var item = Filtered[Selected];
            Close();
            await item.Action();
        }
    }

    /// <summary>Fuzzy match: toda letra do query aparece em ordem.</summary>
    public static bool Matches(string query, string label)
    {
        if (string.IsNullOrEmpty(query))
        {
            return true;
        }

        var qi = 0;
        foreach (var c in label)
        {
            if (qi < query.Length
                && char.ToUpperInvariant(c) == char.ToUpperInvariant(query[qi]))
            {
                qi++;
            }
        }
        return qi == query.Length;
    }

    private void ApplyFilter(string query)
    {
        Filtered = _items
            .Where(i => Matches(query, i.Label)
                || (i.Hint is not null && Matches(query, i.Hint)))
            .OrderByDescending(i => i.Label.StartsWith(
                query, StringComparison.OrdinalIgnoreCase))
            .ThenBy(i => i.Label)
            .ToList();
    }
}
