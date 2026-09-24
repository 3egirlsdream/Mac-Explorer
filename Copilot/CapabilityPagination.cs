using System.Text.Json;

namespace MacExplorer.Copilot;

public sealed record CapabilityPage(int Offset, int ReturnedCount, int TotalCount, int? NextOffset)
{
    public bool HasMore => NextOffset.HasValue;
}

/// <summary>Keep candidate Data as an array; disclose limits separately for tools and saved artifacts.</summary>
internal static class CapabilityPagination
{
    private const int PageSize = 200;

    internal static CapabilityResult Create<T>(IReadOnlyList<T> items, JsonElement arguments,
        Func<T, object> project)
    {
        var offset = 0;
        if (arguments.TryGetProperty("offset", out var value)
            && (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out offset) || offset < 0))
            throw new ArgumentException("offset 必须为非负整数。");
        if (offset > items.Count)
            throw new ArgumentOutOfRangeException(nameof(offset), "offset 超出列表末尾；列表可能已变化，请从 0 重新读取。");

        var data = items.Skip(offset).Take(PageSize).Select(project).ToArray();
        var next = offset + data.Length;
        return new CapabilityResult(true, $"共 {items.Count} 项，本页返回 {data.Length} 项（offset={offset}）", data)
        {
            Page = new CapabilityPage(offset, data.Length, items.Count, next < items.Count ? next : null)
        };
    }
}
