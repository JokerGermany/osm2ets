// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using TruckLib;
using TruckLib.ScsMap;

namespace Osm2Ets.Writer;

/// <summary>
/// Item-Flags, für die TruckLib 0.5.1 keine Eigenschaft hat. Sie stehen im kDOP-Flagfeld des Items,
/// das TruckLib nur intern führt (<c>MapItem.Kdop.Flags</c>); Zugriff deshalb per Reflection.
/// Fehlen die Felder (andere TruckLib-Version), bricht der Writer mit einer klaren Meldung ab,
/// statt das Flag stillschweigend nicht zu setzen.
/// </summary>
internal static class ItemFlags
{
    /// <summary>
    /// Prefab-Flag „Ampeln“ (kDOP-Bit 19): Erst damit stellt das Spiel (und der Editor) die Ampeln eines
    /// Prefabs mit Ampelprofil auf. Der Editor setzt es, sobald man dort ein Ampelprofil wählt; TruckLib
    /// 0.5.1 schreibt es immer gelöscht. Ab TruckLib 0.5.3 heißt es <c>Prefab.Semaphores</c>.
    /// </summary>
    public const int PrefabSemaphores = 19;

    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly PropertyInfo KdopProp = typeof(MapItem).GetProperty("Kdop", Any)
        ?? throw new InvalidOperationException("TruckLib: MapItem.Kdop nicht gefunden (Item-Flags nicht setzbar)");
    private static readonly FieldInfo FlagsField = KdopProp.PropertyType.GetField("Flags", Any)
        ?? throw new InvalidOperationException("TruckLib: KdopItem.Flags nicht gefunden (Item-Flags nicht setzbar)");

    /// <summary>Das ganze Flagfeld (für Prüfungen).</summary>
    public static uint Bits(MapItem item) => ((FlagField)FlagsField.GetValue(KdopProp.GetValue(item))!).Bits;

    public static bool Get(MapItem item, int bit) => ((FlagField)FlagsField.GetValue(KdopProp.GetValue(item))!)[bit];

    public static void Set(MapItem item, int bit, bool value)
    {
        var kdop = KdopProp.GetValue(item)!;
        var flags = (FlagField)FlagsField.GetValue(kdop)!;   // Struktur: Kopie ändern, zurückschreiben
        flags[bit] = value;
        FlagsField.SetValue(kdop, flags);
    }
}
