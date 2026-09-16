using System.Collections.Immutable;
using Eota.Kernel.Content;
using Eota.Kernel.Primitives;

namespace Eota.Kernel.Matches;

public readonly record struct DeckEntry(CardPrototypeId CardId, int Copies);

public sealed record DeckDefinition(Profession Profession, ImmutableArray<DeckEntry> Entries)
{
    public static DeckDefinition Create(Profession profession, IEnumerable<DeckEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return new DeckDefinition(profession, entries.ToImmutableArray());
    }
}
