using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Eota.Content.Compiler;
using Eota.Kernel.Content;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;
using Eota.Kernel.Protocols;
using Eota.Server.Application;
using Eota.Transport.Contracts;

namespace Eota.Server.Infrastructure;

public sealed record HostedRoomConfiguration(int Version, string MatchId, string Name, ulong Seed, string RuleContentHash,
    string ProtocolJson, JsonElement[] Cards, string PlayerOneCredentialHash, string PlayerTwoCredentialHash, string SpectatorCredentialHash,
    RoomDeck? PlayerOneDeck = null, RoomDeck? PlayerTwoDeck = null, RoomTiming? Timing = null);
public sealed record LockedRoomDecks(RoomAdmission? One, RoomAdmission? Two);

// Admission carries deck IDs and rule fingerprints, never executable card definitions.
public sealed partial class HostedRoom : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HostedRoomConfiguration _config;
    private readonly RuleContentPack _content;
    private readonly CompiledGameProtocol _protocol;
    private readonly string _directory;
    private RoomAdmission? _one;
    private RoomAdmission? _two;
    public MatchActor? Actor { get; private set; }
    public string MatchId => _config.MatchId;
    public async Task WriteOperationsAsync(CancellationToken token = default)
    {
        if (Actor is not { } actor) { return; }
        var report = await actor.GetOperationsAsync(token).ConfigureAwait(false);
        AtomicFile.Write(Path.Combine(_directory, "runtime-diagnostics.json"), JsonSerializer.SerializeToUtf8Bytes(report));
    }

    public HostedRoom(string configurationPath, TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        if (new FileInfo(configurationPath).Length > 8 * 1024 * 1024) { throw new InvalidDataException("room-config-too-large"); }
        _directory = Path.GetDirectoryName(Path.GetFullPath(configurationPath))!;
        _config = JsonSerializer.Deserialize<HostedRoomConfiguration>(File.ReadAllText(configurationPath)) ?? throw new InvalidDataException("invalid-room");
        if (_config.Version != 1 || _config.Name is not { Length: > 0 and <= 80 } || !CardPrototypeId.TryParse(_config.MatchId, out _)
            || _config.Cards is not { Length: > 0 and <= 2048 }
            || !new[] { _config.PlayerOneCredentialHash, _config.PlayerTwoCredentialHash, _config.SpectatorCredentialHash }.All(MatchHandshake.IsRuleContentHash))
        { throw new InvalidDataException("invalid-room"); }
        var content = CardContentCompiler.Compile(_config.Cards.Select((card, index) => new ContentSourceDocument(index.ToString(System.Globalization.CultureInfo.InvariantCulture), card.GetRawText())));
        var protocol = ProtocolCompiler.Compile("room-protocol", _config.ProtocolJson);
        if (!content.IsSuccess || !protocol.IsSuccess || content.Content!.Rules.Hash.ToString() != _config.RuleContentHash)
        { throw new InvalidDataException("room-content-invalid"); }
        _content = content.Content.Rules; _protocol = protocol.Protocol!;
        _timing = _config.Timing ?? new RoomTiming();
        _settingsHash = _timing.ComputeHash(_config.RuleContentHash, _protocol.Hash.ToString());
        var locked = Path.Combine(_directory, "admission.json");
        if (File.Exists(locked))
        {
            var decks = JsonSerializer.Deserialize<LockedRoomDecks>(File.ReadAllText(locked)) ?? throw new InvalidDataException("invalid-admission");
            if (decks.One is not null) { Validate(decks.One); _one = decks.One; }
            if (decks.Two is not null) { Validate(decks.Two); _two = decks.Two; }
            ValidateScope(_one, _two);
            CreateWhenReady();
        }
    }

    public bool Authenticate(Audience audience, string? token)
    {
        if (token is not { Length: 64 } || !MatchHandshake.IsRuleContentHash(token)) { return false; }
        var expected = audience switch { Audience.PlayerOne => _config.PlayerOneCredentialHash, Audience.PlayerTwo => _config.PlayerTwoCredentialHash, _ => _config.SpectatorCredentialHash };
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(token)), Convert.FromHexString(expected));
    }

    public async Task<RoomDescription> DescribeAsync(Audience audience, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try { return Describe(audience); } finally { _gate.Release(); }
    }

    public async Task<RoomDescription> AdmitAsync(Audience audience, RoomAdmission admission, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (audience == Audience.Spectator) { throw new InvalidDataException("spectator-read-only"); }
            Validate(admission);
            var previous = audience == Audience.PlayerOne ? _one : _two;
            if (previous is not null && DeckHash(previous.Deck) != DeckHash(admission.Deck)) { throw new InvalidDataException("deck-already-locked"); }
            var nextOne = audience == Audience.PlayerOne ? admission : _one;
            var nextTwo = audience == Audience.PlayerTwo ? admission : _two;
            ValidateScope(nextOne, nextTwo);
            AtomicFile.Write(Path.Combine(_directory, "admission.json"), JsonSerializer.SerializeToUtf8Bytes(new LockedRoomDecks(nextOne, nextTwo)));
            _one = nextOne; _two = nextTwo;
            CreateWhenReady();
            return Describe(audience);
        }
        finally { _gate.Release(); }
    }

    private RoomDescription Describe(Audience audience) => new(1, MatchId, _config.Name, _config.RuleContentHash, _protocol.Hash.ToString(),
        _config.ProtocolJson, _one is not null, _two is not null, Actor is not null,
        (audience == Audience.PlayerOne ? _one : audience == Audience.PlayerTwo ? _two : null) is { } own ? DeckHash(own.Deck) : null,
        audience == Audience.PlayerOne ? _one?.Deck ?? _config.PlayerOneDeck : audience == Audience.PlayerTwo ? _two?.Deck ?? _config.PlayerTwoDeck : null,
        _timing, _settingsHash);

    private void Validate(RoomAdmission admission)
    {
        if (admission.Version != 1) { throw new InvalidDataException("room-version-mismatch"); }
        if (!MatchHandshake.IsRuleContentHash(admission.RuleContentHash)) { throw new InvalidDataException("invalid-rule-hash"); }
        if (admission.CardRules is null)
        { throw new InvalidDataException("rule-manifest-required"); }
        if (admission.CardRules.Count is < 1 or > 2048
            || admission.CardRules.Any(pair => !CardPrototypeId.TryParse(pair.Key, out _) || !MatchHandshake.IsRuleContentHash(pair.Value)))
        { throw new InvalidDataException("invalid-rule-manifest"); }
        if (admission.ProtocolHash != _protocol.Hash.ToString()) { throw new InvalidDataException("protocol-mismatch"); }
        if (admission.RoomSettingsHash != _settingsHash)
        { throw new InvalidDataException("room-settings-mismatch"); }
        var deck = ToDeck(admission.Deck);
        var errors = MatchFactory.Create(new MatchCreationRequest(_protocol, _content, _config.Seed, deck, deck)).Errors;
        if (!errors.IsEmpty) { throw new InvalidDataException("invalid-deck: " + string.Join(", ", errors.Select(e => e.DetailCode).Distinct(StringComparer.Ordinal))); }
    }

    private void ValidateScope(RoomAdmission? one, RoomAdmission? two)
    {
        var roots = new[] { one, two }.Where(value => value is not null)
            .SelectMany(value => value!.Deck.Cards).Select(card => CardPrototypeId.Parse(card.Id));
        var scope = CardRuleScope.Closure(_content, roots);
        foreach (var (admission, seat) in new[] { (one, "playerOne"), (two, "playerTwo") })
        {
            if (admission is null) { continue; }
            var manifest = admission.CardRules;
            if (scope.Any(id => !manifest.TryGetValue(id.Value, out var hash) || hash != _content.CardRuleHash(id).ToString()))
            { throw new InvalidDataException($"deck-rule-mismatch: {seat} 的内容与本局卡组或衍生卡不一致，请同步相关卡牌后重试。"); }
        }
    }

    public static DeckDefinition ToDeck(RoomDeck deck)
    {
        if (deck is null || !Enum.TryParse<Profession>(deck.Profession, out var profession) || !Enum.IsDefined(profession)
            || profession == Profession.Neutral || deck.Cards.IsDefaultOrEmpty || deck.Cards.Length > 256
            || deck.Cards.Any(c => c is null || c.Copies is < 1 or > 256 || !CardPrototypeId.TryParse(c.Id, out _)))
        { throw new InvalidDataException("invalid-deck"); }
        return DeckDefinition.Create(profession, deck.Cards.Select(c => new DeckEntry(CardPrototypeId.Parse(c.Id), c.Copies)));
    }

    public static string DeckHash(RoomDeck deck)
    {
        _ = ToDeck(deck);
        var canonical = ContractJson.Serialize(deck with { Cards = [.. deck.Cards.OrderBy(c => c.Id, StringComparer.Ordinal)] });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private void CreateWhenReady()
    {
        if (Actor is not null || _one is null || _two is null) { return; }
        var request = new MatchCreationRequest(_protocol, _content, _config.Seed, ToDeck(_one.Deck), ToDeck(_two.Deck));
        var journal = new FileMatchJournal(_directory, request);
        Actor = new MatchActor(MatchId, request, journal: journal, timeProvider: _time);
    }

    public async ValueTask DisposeAsync()
    {
        if (Actor is not null) { await Actor.DisposeAsync().ConfigureAwait(false); }
        _gate.Dispose();
    }
}
