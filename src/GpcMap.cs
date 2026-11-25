// SPDX-License-Identifier: GPL-3.0-only
// Additional permission under GPLv3 section 7: see NVAPI-EXCEPTION.txt.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Rtx50Temps;

internal sealed record GpcIds(int selector, int? logical, int? hardware)
{
    public string association => hardware.HasValue ? "matching_chiplet_id" : "unconfirmed";
    public Caption Label() => logical.HasValue && hardware.HasValue
        ? new($"逻辑 GPC {logical} · 硬件 GPC {hardware}（选择器同号）", $"Logical GPC {logical} · hardware GPC {hardware} (matching selector ID)")
        : new("逻辑 / 硬件 GPC 未确认", "Logical / hardware GPC unconfirmed");
}

internal sealed class GpcMap
{
    private const int MaxGpcs = 12;
    private const uint GpcMask = (1u << MaxGpcs) - 1;
    internal readonly int[] LogicalToHardware;
    private readonly bool selectorsMatch;

    private GpcMap(int[] logicalToHardware, IEnumerable<int> selectors)
    {
        LogicalToHardware = logicalToHardware;
        selectorsMatch = logicalToHardware != null && selectors.ToHashSet().SetEquals(logicalToHardware);
    }

    internal static GpcMap Read(Nvidia api, Card card, IEnumerable<int> selectors)
    {
        uint? disabled = api.ReadRegs(card, 0x820c1c)[0];
        if (!disabled.HasValue) return new(null, selectors);
        uint enabled = ~disabled.Value & GpcMask;
        int count = BitOperations.PopCount(enabled);
        if (count == 0) return new(null, selectors);

        // The table is indexed by compute-logical GPC. Bits 20:16 hold the chiplet ID.
        var addresses = Enumerable.Range(0, count).Select(i => 0xcc00 + 4*i).ToArray();
        return new(Decode(enabled, api.ReadRegs(card, addresses)), selectors);
    }

    internal static int[] Decode(uint enabled, uint?[] words)
    {
        if (enabled == 0 || (enabled & ~GpcMask) != 0 || words.Length != BitOperations.PopCount(enabled))
            return null;
        var result = new int[words.Length];
        uint remaining = enabled;
        for (int logical = 0; logical < words.Length; logical++)
        {
            if (words[logical] is not uint word) return null;
            int hardware = (int)((word >> 16) & 31);
            uint bit = 1u << hardware;
            if ((remaining & bit) == 0) return null;
            remaining &= ~bit;
            result[logical] = hardware;
        }
        return result;
    }

    internal GpcIds ForSelector(int selector)
    {
        int logical = selectorsMatch ? Array.IndexOf(LogicalToHardware, selector) : -1;
        return logical >= 0 ? new(selector, logical, selector) : new(selector, null, null);
    }
}
